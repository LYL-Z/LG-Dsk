using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace GlassDesk.AppServices.Updates;

public sealed partial class UpdateClient
{
    public async Task<UpdateRelease?> CheckAsync(Version current, CancellationToken token)
    {
        if (!_useStaticFeed) return await CheckGitHubAsync(current, token).ConfigureAwait(false);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        lifetime.CancelAfter(TimeSpan.FromSeconds(18));
        // The publisher CDN starts first; GitHub is hedged rather than waiting for
        // a dead route to exhaust three 30-second attempts.
        var checks = new List<Task<(UpdateRelease? Release, Exception? Error)>>
        {
            Observe(() => CheckStaticAsync(current, lifetime.Token)),
            Observe(async () =>
            {
                await Task.Delay(500, lifetime.Token).ConfigureAwait(false);
                return await CheckGitHubAsync(current, lifetime.Token).ConfigureAwait(false);
            }),
        };
        var validResponse = false;
        Exception? lastFailure = null;
        try
        {
            while (checks.Count > 0)
            {
                var next = await Task.WhenAny(checks).ConfigureAwait(false);
                checks.Remove(next);
                var result = await next.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (result.Error is null) validResponse = true;
                else lastFailure = result.Error;
                if (result.Release is not null) return result.Release;
            }
            if (validResponse) return null;
            throw new IOException("更新服务暂不可达。已尝试独立分发源与 GitHub；可稍后重试或使用离线安装包。", lastFailure);
        }
        finally { lifetime.Cancel(); }

        static async Task<(UpdateRelease?, Exception?)> Observe(Func<Task<UpdateRelease?>> action)
        {
            try { return (await action().ConfigureAwait(false), null); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or OperationCanceledException)
            { return (null, ex); }
        }
    }

    private async Task<UpdateRelease?> CheckStaticAsync(Version current, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var request = Request(new Uri(SignedUpdateFeed.ProductionSource, "stable.json"), metadata: false);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { } redirect
            && redirect != request.RequestUri) throw new InvalidDataException("更新目录不接受重定向。");
        var data = await ReadBoundedAsync(response.Content, MaximumMetadataSize, deadline.Token).ConfigureAwait(false);
        return SignedUpdateFeed.Parse(Encoding.UTF8.GetString(data), current,
            SignedUpdateFeed.ProductionSource, SignedUpdateFeed.ReadPublisherKey(), DateTimeOffset.UtcNow);
    }

    public async Task<string> DownloadAsync(UpdateRelease release, string cacheRoot,
        IProgress<UpdateDownloadProgress>? progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(release);
        ValidateInstaller(release);
        var directory = Path.Combine(Path.GetFullPath(cacheRoot), $"LG-Dsk-{release.Version}-{release.Sha256[..12]}");
        Directory.CreateDirectory(directory);
        // Serialize attempts using the same package directory across processes as well.
        try
        {
            await using var ownership = new FileStream(Path.Combine(directory, "download.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            if (release.ChunkSource is not null && release.Chunks.Count > 0)
            {
                SignedUpdateFeed.ValidateSource(release.ChunkSource);
                SignedUpdateFeed.ValidateChunks(release.Chunks, release.Size);
                try { return await DownloadChunksAsync(release, directory, progress, token).ConfigureAwait(false); }
                catch (Exception ex) when (!token.IsCancellationRequested && ex is IOException or InvalidDataException or HttpRequestException)
                {
                    // Per-chunk validated cache survives the fallback and any later user retry.
                    progress?.Report(new UpdateDownloadProgress(0, 0, release.Size, 1, true)
                        { Source = "GitHub 备用源" });
                }
            }
            return await DownloadGitHubAsync(release, cacheRoot, progress, token).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            // The ownership stream has closed before removing an empty failed attempt.
            // Keep verified chunks when a later transfer can reuse them.
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
            throw;
        }
    }

    private async Task<string> DownloadChunksAsync(UpdateRelease release, string directory,
        IProgress<UpdateDownloadProgress>? progress, CancellationToken token)
    {
        var final = Path.Combine(directory, release.FileName);
        if (await VerifyFileAsync(final, release, token).ConfigureAwait(false))
        {
            progress?.Report(new UpdateDownloadProgress(100, release.Size, release.Size, 1, true) { Source = "已校验缓存" });
            return final;
        }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        lifetime.CancelAfter(MaximumDownloadDuration);
        var received = new long[release.Chunks.Count];
        var paths = new string[release.Chunks.Count];
        var sync = new object();
        var lastAt = DateTimeOffset.MinValue;
        var lastPercent = -1;
        void Report(int index, long bytes, int attempt, bool resumed)
        {
            lock (sync)
            {
                received[index] = bytes;
                var sum = received.Sum();
                var percent = Percent(sum, release.Size);
                if (percent == lastPercent && DateTimeOffset.UtcNow - lastAt < TimeSpan.FromMilliseconds(250)) return;
                lastAt = DateTimeOffset.UtcNow;
                lastPercent = percent;
                progress?.Report(new UpdateDownloadProgress(percent, sum, release.Size, attempt, resumed)
                    { Source = "LG Dsk 静态分发 · 最多 3 路并行" });
            }
        }
        try
        {
            var groups = Enumerable.Range(0, release.Chunks.Count)
                .GroupBy(i => release.Chunks[i].Sha256, StringComparer.OrdinalIgnoreCase);
            await Parallel.ForEachAsync(groups,
                new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = lifetime.Token }, async (group, ct) =>
                {
                    var indices = group.ToArray();
                    var path = await DownloadChunkAsync(release.Chunks[indices[0]], release.ChunkSource!, directory,
                        (n, attempt, resumed) =>
                        {
                            foreach (var i in indices) Report(i, n, attempt, resumed);
                        }, ct).ConfigureAwait(false);
                    foreach (var i in indices) paths[i] = path;
                }).ConfigureAwait(false);
            var assembled = Path.Combine(directory, "assembled.partial");
            await using (var output = new FileStream(assembled, FileMode.Create, FileAccess.Write, FileShare.None,
                131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
                foreach (var path in paths)
                {
                    await using var input = File.OpenRead(path);
                    await input.CopyToAsync(output, lifetime.Token).ConfigureAwait(false);
                }
            if (!await VerifyFileAsync(assembled, release, lifetime.Token).ConfigureAwait(false))
            {
                File.Delete(assembled);
                throw new InvalidDataException("合并后的安装包校验失败，未执行安装。");
            }
            lifetime.Token.ThrowIfCancellationRequested();
            File.Move(assembled, final, overwrite: true);
            progress?.Report(new UpdateDownloadProgress(100, release.Size, release.Size, 1, false) { Source = "安装包校验完成" });
            return final;
        }
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
        { throw new IOException("静态分发连接超时，保留进度并尝试备用源。", ex); }
    }

    private async Task<string> DownloadChunkAsync(UpdateChunk chunk, Uri source, string directory,
        Action<long, int, bool> progress, CancellationToken token)
    {
        var file = Path.Combine(directory, chunk.Sha256.ToLowerInvariant() + ".chunk");
        var partial = file + ".partial";
        if (await VerifyChunkAsync(file, chunk, token).ConfigureAwait(false)) { progress(chunk.Size, 1, true); return file; }
        Exception? failure = null;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            if (offset > chunk.Size) { File.Delete(partial); offset = 0; }
            if (offset == chunk.Size)
            {
                if (await VerifyChunkAsync(partial, chunk, token).ConfigureAwait(false))
                { File.Move(partial, file, true); progress(chunk.Size, attempt, true); return file; }
                File.Delete(partial);
                offset = 0;
            }
            try
            {
                using var request = Request(new Uri(source, $"chunks/{chunk.Sha256.ToLowerInvariant()}.bin"), metadata: false);
                if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
                using var headers = CancellationTokenSource.CreateLinkedTokenSource(token);
                headers.CancelAfter(TimeSpan.FromSeconds(12));
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false);
                if (response.RequestMessage?.RequestUri is { } redirect && redirect != request.RequestUri)
                    throw new InvalidDataException("分块下载不接受重定向。");
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && offset > 0)
                {
                    // The CDN no longer has the entity/range represented by this cached
                    // partial. Drop only this chunk's unverified bytes and fetch it whole.
                    File.Delete(partial);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.StatusCode == HttpStatusCode.OK) offset = 0;
                else if (response.StatusCode != HttpStatusCode.PartialContent || offset == 0
                    || response.Content.Headers.ContentRange?.From != offset
                    || response.Content.Headers.ContentRange?.Length != chunk.Size)
                    throw new InvalidDataException("分块续传范围无效。");
                if (response.Content.Headers.ContentLength is { } length && length != chunk.Size - offset)
                    throw new InvalidDataException("分块大小无效。");
                await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
                await using (var output = new FileStream(partial, offset == 0 ? FileMode.Create : FileMode.Open,
                    FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    output.Position = offset;
                    var resumed = offset > 0;
                    progress(offset, attempt, resumed);
                    var buffer = new byte[131072];
                    while (true)
                    {
                        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
                        idle.CancelAfter(TimeSpan.FromSeconds(20));
                        var count = await input.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                        if (count == 0) break;
                        if (count > chunk.Size - offset) throw new InvalidDataException("分块超出声明大小。");
                        await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                        offset += count;
                        progress(offset, attempt, resumed);
                    }
                }
                if (offset != chunk.Size)
                    throw new IOException("分块连接提前关闭，保留已下载部分以便续传。");
                if (!await VerifyChunkAsync(partial, chunk, token).ConfigureAwait(false))
                    throw new InvalidDataException("分块校验失败。");
                File.Move(partial, file, overwrite: true);
                return file;
            }
            catch (Exception ex) when (!token.IsCancellationRequested
                && ex is IOException or InvalidDataException or HttpRequestException or OperationCanceledException)
            {
                failure = ex;
                if (ex is InvalidDataException && File.Exists(partial)) File.Delete(partial);
                if (attempt < 4) await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), token).ConfigureAwait(false);
            }
        }
        throw new IOException("分块下载多次失败，保留已有进度。", failure);
    }

    private static async Task<bool> VerifyChunkAsync(string path, UpdateChunk chunk, CancellationToken token)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != chunk.Size) return false;
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false))
            .Equals(chunk.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken token)
    {
        if (content.Headers.ContentLength > maximum) throw new InvalidDataException("更新信息过大。");
        await using var input = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (count > maximum - output.Length) throw new InvalidDataException("更新信息过大。");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}
