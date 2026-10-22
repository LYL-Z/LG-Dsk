using System.Net.Http;
using System.Net.Http.Headers;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GlassDesk.AppServices.Updates;

/// <summary>Stable GitHub releases and bounded, checksum-verified installer downloads.</summary>
public sealed partial class UpdateClient
{
    public const long MaximumInstallerSize = 512L * 1024 * 1024;
    private const int MaximumMetadataSize = 512 * 1024;
    private const int MaximumDownloadAttempts = 6;
    private static readonly TimeSpan MetadataAttemptTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DownloadReadIdleTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan MaximumDownloadDuration = TimeSpan.FromMinutes(45);
    private readonly HttpClient _http;
    private readonly Uri _source;
    private readonly bool _useStaticFeed;

    public UpdateClient(HttpClient http, Uri source, bool useStaticFeed = false)
    {
        ArgumentNullException.ThrowIfNull(http);
        Repository(source);
        _http = http;
        _source = source;
        _useStaticFeed = useStaticFeed;
    }

    private async Task<UpdateRelease?> CheckGitHubAsync(Version current, CancellationToken token)
    {
        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(MetadataAttemptTimeout);
                using var request = Request(_source, metadata: true);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (IsTransientStatus(response.StatusCode))
                {
                    lastFailure = new HttpRequestException($"更新源暂时返回 HTTP {(int)response.StatusCode}。");
                    if (attempt == 3) break;
                    await Task.Delay(RetryDelay(attempt, response.Headers.RetryAfter), token).ConfigureAwait(false);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaximumMetadataSize)
                    throw new InvalidDataException("更新信息过大。");
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var output = new MemoryStream();
                var buffer = new byte[16384];
                int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
                {
                    if (output.Length + read > MaximumMetadataSize) throw new InvalidDataException("更新信息过大。");
                    output.Write(buffer, 0, read);
                }
                return ParseRelease(System.Text.Encoding.UTF8.GetString(output.ToArray()), current, _source);
            }
            catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
            {
                lastFailure = ex;
                if (attempt == 3) break;
                await Task.Delay(RetryDelay(attempt, null), token).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (IsTransientException(ex))
            {
                lastFailure = ex;
                if (attempt == 3) break;
                await Task.Delay(RetryDelay(attempt, null), token).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                lastFailure = ex;
                if (attempt == 3) break;
                await Task.Delay(RetryDelay(attempt, null), token).ConfigureAwait(false);
            }
        }
        token.ThrowIfCancellationRequested();
        throw new HttpRequestException("无法连接到 LG Dsk 发布源；网络恢复后可以重新检查。", lastFailure);
    }

    public static UpdateRelease? ParseRelease(string json, Version current, Uri source)
    {
        var repository = Repository(source);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
            var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
            var text = tag.StartsWith('v') ? tag[1..] : tag;
            if (!StableVersionPattern().IsMatch(text) || !Version.TryParse(text, out var version))
                throw new InvalidDataException("更新版本号无效。仅支持正式版本。");
            // Compare product version, not the intentionally stable assembly identity (0.2.2.0).
            var installed = new Version(current.Major, current.Minor, Math.Max(0, current.Build));
            if (version <= installed) return null;
            var fileName = $"LG-Dsk-{version}-win-x64-Setup.exe";
            var assets = root.GetProperty("assets").EnumerateArray()
                .Where(asset => asset.GetProperty("name").GetString() == fileName).ToArray();
            if (assets.Length != 1) throw new InvalidDataException("此版本没有唯一的 Windows x64 安装包。");
            var asset = assets[0];
            var size = asset.GetProperty("size").GetInt64();
            if (size <= 0 || size > MaximumInstallerSize || asset.GetProperty("state").GetString() != "uploaded")
                throw new InvalidDataException("更新安装包尚未就绪或大小无效。");
            var digest = asset.GetProperty("digest").GetString() ?? string.Empty;
            if (!DigestPattern().IsMatch(digest)) throw new InvalidDataException("更新安装包缺少 SHA-256 校验信息。");
            var url = asset.GetProperty("browser_download_url").GetString();
            var expected = $"https://github.com/{repository}/releases/download/{tag}/{fileName}";
            var page = root.GetProperty("html_url").GetString();
            if (url != expected || page != $"https://github.com/{repository}/releases/tag/{tag}")
                throw new InvalidDataException("更新地址不属于配置的发布仓库。");
            var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty;
            return new UpdateRelease(version, fileName, new Uri(url), digest[7..].ToLowerInvariant(), size,
                notes.Length > 12000 ? notes[..12000] : notes, new Uri(page!));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidDataException("更新信息格式无效。", ex);
        }
    }

    private async Task<string> DownloadGitHubAsync(UpdateRelease release, string cacheRoot,
        IProgress<UpdateDownloadProgress>? progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(release);
        var repository = Repository(_source);
        if (release.DownloadUrl.AbsoluteUri != $"https://github.com/{repository}/releases/download/v{release.Version}/{release.FileName}"
            && release.DownloadUrl.AbsoluteUri != $"https://github.com/{repository}/releases/download/{release.Version}/{release.FileName}")
            throw new InvalidDataException("更新下载地址无效。");
        ValidateInstaller(release);
        // Reuse a product/version/digest-specific partial file so a user retry continues from
        // the last verified byte. The SHA-256 is part of the directory identity and is checked
        // again before the installer can be started.
        var directory = Path.Combine(Path.GetFullPath(cacheRoot), $"LG-Dsk-{release.Version}-{release.Sha256[..12]}");
        Directory.CreateDirectory(directory);
        var partial = Path.Combine(directory, "installer.partial");
        var final = Path.Combine(directory, release.FileName);
        try
        {
            if (File.Exists(final) && await VerifyFileAsync(final, release, token).ConfigureAwait(false))
            {
                progress?.Report(new UpdateDownloadProgress(100, release.Size, release.Size, 1, false));
                return final;
            }
            if (File.Exists(final)) File.Delete(final);

            using var duration = CancellationTokenSource.CreateLinkedTokenSource(token);
            duration.CancelAfter(MaximumDownloadDuration);
            Exception? lastFailure = null;
            var entityTag = (string?)null;
            var lastPercent = -1;
            var lastProgressAt = DateTimeOffset.MinValue;
            var restartedForMismatch = false;
            for (var attempt = 1; attempt <= MaximumDownloadAttempts; attempt++)
            {
                duration.Token.ThrowIfCancellationRequested();
                var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                if (offset > release.Size)
                {
                    File.Delete(partial);
                    offset = 0;
                }
                if (offset == release.Size)
                {
                    if (await VerifyFileAsync(partial, release, duration.Token).ConfigureAwait(false))
                    {
                        File.Move(partial, final, overwrite: true);
                        progress?.Report(new UpdateDownloadProgress(100, release.Size, release.Size, attempt, true));
                        return final;
                    }
                    if (restartedForMismatch) throw new InvalidDataException("断点续传内容未通过 SHA-256 校验，请删除临时更新文件后重试。");
                    File.Delete(partial);
                    restartedForMismatch = true;
                    offset = 0;
                }

                try
                {
                    using var request = Request(release.DownloadUrl, metadata: false);
                    if (offset > 0)
                    {
                        request.Headers.Range = new RangeHeaderValue(offset, null);
                        if (EntityTagHeaderValue.TryParse(entityTag, out var tag) && !tag.IsWeak)
                            request.Headers.IfRange = new RangeConditionHeaderValue(tag);
                    }

                    using var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(duration.Token);
                    headersTimeout.CancelAfter(TimeSpan.FromSeconds(60));
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersTimeout.Token).ConfigureAwait(false);
                    if (IsTransientStatus(response.StatusCode))
                    {
                        lastFailure = new HttpRequestException($"更新文件服务器暂时返回 HTTP {(int)response.StatusCode}。");
                        if (attempt == MaximumDownloadAttempts) break;
                        progress?.Report(new UpdateDownloadProgress(Percent(offset, release.Size), offset, release.Size, attempt + 1, offset > 0));
                        await Task.Delay(RetryDelay(attempt, response.Headers.RetryAfter), duration.Token).ConfigureAwait(false);
                        continue;
                    }
                    if (response.RequestMessage?.RequestUri is { Scheme: not "https" })
                        throw new InvalidDataException("更新下载被重定向到不安全的地址。");

                    if (offset > 0 && response.StatusCode == HttpStatusCode.OK)
                    {
                        // A CDN/proxy may ignore Range. Its 200 body starts at byte zero, so
                        // replace the partial instead of appending a duplicate response.
                        offset = 0;
                    }
                    else if (offset > 0)
                    {
                        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                        {
                            File.Delete(partial);
                            entityTag = null;
                            continue;
                        }
                        var range = response.Content.Headers.ContentRange;
                        if (response.StatusCode != HttpStatusCode.PartialContent || range?.From != offset
                            || range.Length != release.Size || !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("更新服务器返回的续传范围与本地进度不一致。");
                    }
                    else if (response.StatusCode != HttpStatusCode.OK)
                    {
                        response.EnsureSuccessStatusCode();
                        throw new InvalidDataException("更新服务器没有返回完整安装包。");
                    }

                    var responseTag = response.Headers.ETag?.ToString();
                    if (offset > 0 && entityTag is not null && responseTag is not null
                        && !string.Equals(entityTag, responseTag, StringComparison.Ordinal))
                    {
                        File.Delete(partial);
                        entityTag = responseTag;
                        continue;
                    }
                    entityTag ??= responseTag;
                    var remaining = release.Size - offset;
                    if (response.Content.Headers.ContentLength is { } length && length != remaining)
                        throw new InvalidDataException("安装包大小与更新信息不一致。");

                    await using var input = await response.Content.ReadAsStreamAsync(duration.Token).ConfigureAwait(false);
                    var mode = offset == 0 ? FileMode.Create : FileMode.Open;
                    await using var output = new FileStream(partial, mode, FileAccess.Write, FileShare.Read,
                        131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (offset > 0) output.Position = offset;
                    var received = offset;
                    var buffer = new byte[131072];
                    progress?.Report(new UpdateDownloadProgress(Percent(received, release.Size), received, release.Size, attempt, offset > 0));
                    int read;
                    while (true)
                    {
                        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(duration.Token);
                        readTimeout.CancelAfter(DownloadReadIdleTimeout);
                        try
                        {
                            read = await input.ReadAsync(buffer, readTimeout.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException ex) when (!duration.IsCancellationRequested && !token.IsCancellationRequested)
                        {
                            throw new IOException("下载连接长时间没有传输数据。", ex);
                        }
                        if (read == 0) break;
                        received += read;
                        if (received > release.Size) throw new InvalidDataException("安装包超出声明大小。");
                        await output.WriteAsync(buffer.AsMemory(0, read), duration.Token).ConfigureAwait(false);
                        var percent = Percent(received, release.Size);
                        var now = DateTimeOffset.UtcNow;
                        if (percent != lastPercent || now - lastProgressAt >= TimeSpan.FromSeconds(1))
                        {
                            progress?.Report(new UpdateDownloadProgress(percent, received, release.Size, attempt, offset > 0));
                            lastPercent = percent;
                            lastProgressAt = now;
                        }
                    }
                    await output.FlushAsync(duration.Token).ConfigureAwait(false);
                    if (received != release.Size)
                        throw new IOException("远程主机提前关闭了连接；已保留已下载部分，正在尝试断点续传。");

                    if (!await VerifyFileAsync(partial, release, duration.Token).ConfigureAwait(false))
                    {
                        if (restartedForMismatch)
                        {
                            File.Delete(partial);
                            throw new InvalidDataException("安装包 SHA-256 校验失败，请重新检查更新源。");
                        }
                        File.Delete(partial);
                        entityTag = null;
                        restartedForMismatch = true;
                        continue;
                    }

                    duration.Token.ThrowIfCancellationRequested();
                    File.Move(partial, final, overwrite: true);
                    progress?.Report(new UpdateDownloadProgress(100, release.Size, release.Size, attempt, offset > 0));
                    return final;
                }
                catch (InvalidDataException)
                {
                    // Preserve partial bytes only for transport interruptions. Invalid content,
                    // malformed ranges and oversized responses must never be resumed later.
                    if (File.Exists(partial)) File.Delete(partial);
                    throw;
                }
                catch (OperationCanceledException ex) when (!token.IsCancellationRequested && !duration.IsCancellationRequested)
                {
                    lastFailure = ex;
                }
                catch (HttpRequestException ex) when (IsTransientException(ex)) { lastFailure = ex; }
                catch (IOException ex) { lastFailure = ex; }

                if (attempt == MaximumDownloadAttempts) break;
                var retainedBytes = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                progress?.Report(new UpdateDownloadProgress(Percent(retainedBytes, release.Size), retainedBytes,
                    release.Size, attempt + 1, retainedBytes > 0));
                await Task.Delay(RetryDelay(attempt, null), duration.Token).ConfigureAwait(false);
            }

            duration.Token.ThrowIfCancellationRequested();
            throw new IOException("更新文件多次中断。已保留已下载部分；请检查网络后重试，程序会从断点继续。", lastFailure);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (InvalidDataException)
        {
            // Remove invalid installer bytes, while retaining independently verified
            // content-addressed chunks from a previous static-source attempt.
            if (File.Exists(partial)) File.Delete(partial);
            if (File.Exists(final)) File.Delete(final);
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new IOException("更新下载超过最长时限；已保留下载进度，稍后重试即可续传。", ex);
        }
    }

    private static int Percent(long received, long total) =>
        total <= 0 ? 0 : (int)Math.Clamp(received * 100 / total, 0, 99);

    private static async Task<bool> VerifyFileAsync(string path, UpdateRelease release, CancellationToken token)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != release.Size) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
        return Convert.ToHexString(digest).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTransientStatus(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        || (int)status >= 500;

    private static bool IsTransientException(HttpRequestException exception) =>
        exception.StatusCode is null || IsTransientStatus(exception.StatusCode.Value);

    private static TimeSpan RetryDelay(int attempt, RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is { } delta && delta >= TimeSpan.Zero)
            return delta > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delta;
        if (retryAfter?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay <= TimeSpan.Zero ? TimeSpan.Zero : delay > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delay;
        }
        var baseMilliseconds = Math.Min(8000, 350 * (1 << Math.Min(attempt - 1, 4)));
        return TimeSpan.FromMilliseconds(baseMilliseconds + Random.Shared.Next(0, 251));
    }

    internal static void ValidateInstaller(UpdateRelease release)
    {
        if (!StableVersionPattern().IsMatch(release.Version.ToString())
            || release.FileName != $"LG-Dsk-{release.Version}-win-x64-Setup.exe"
            || !DigestPattern().IsMatch("sha256:" + release.Sha256)
            || release.Size <= 0 || release.Size > MaximumInstallerSize)
            throw new InvalidDataException("安装包信息无效。");
    }

    private static HttpRequestMessage Request(Uri url, bool metadata)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("LG-Dsk-Updater/2.0");
        if (metadata) request.Headers.Accept.ParseAdd("application/vnd.github+json");
        return request;
    }

    private static string Repository(Uri source)
    {
        if (!source.IsAbsoluteUri || source.Scheme != "https" || source.Host != "api.github.com"
            || !source.IsDefaultPort || source.UserInfo.Length != 0 || source.Query.Length != 0 || source.Fragment.Length != 0
            || !SourcePattern().IsMatch(source.AbsolutePath))
            throw new InvalidDataException("更新源必须是 GitHub 的公开仓库 Releases 接口。");
        return string.Join('/', source.AbsolutePath.Split('/').Skip(2).Take(2));
    }

    [GeneratedRegex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")]
    private static partial Regex StableVersionPattern();
    [GeneratedRegex(@"^sha256:[0-9a-fA-F]{64}$")]
    private static partial Regex DigestPattern();
    [GeneratedRegex(@"^/repos/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/releases/latest$")]
    private static partial Regex SourcePattern();
}
