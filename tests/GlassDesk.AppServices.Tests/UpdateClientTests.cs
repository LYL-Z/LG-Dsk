using System.Net;
using System.Security.Cryptography;
using System.Text;
using GlassDesk.AppServices.Updates;

namespace GlassDesk.AppServices.Tests;

public sealed class UpdateClientTests
{
    private static readonly Uri Source = new("https://api.github.com/repos/LYL-Z/LG-Dsk/releases/latest");
    private static string Fixture(string version = "0.2.10", string digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", long size = 4) => $$"""
        {"tag_name":"v{{version}}","draft":false,"prerelease":false,"body":"更新说明",
        "html_url":"https://github.com/LYL-Z/LG-Dsk/releases/tag/v{{version}}",
        "assets":[{"name":"LG-Dsk-{{version}}-win-x64-Setup.exe","state":"uploaded","size":{{size}},"digest":"{{digest}}",
        "browser_download_url":"https://github.com/LYL-Z/LG-Dsk/releases/download/v{{version}}/LG-Dsk-{{version}}-win-x64-Setup.exe"}]}
        """;

    [Fact]
    public void Selects_numeric_newer_version_and_matching_verified_asset()
    {
        var release = UpdateClient.ParseRelease(Fixture(), new Version(0, 2, 9), Source);
        Assert.NotNull(release);
        Assert.Equal(new Version(0, 2, 10), release.Version);
        Assert.Equal("更新说明", release.Notes);
        Assert.Equal("LG-Dsk-0.2.10-win-x64-Setup.exe", release.FileName);
    }

    [Theory]
    [InlineData("0.2.8")]
    [InlineData("0.2.9")]
    public void Never_offers_equal_or_older_versions(string version) =>
        Assert.Null(UpdateClient.ParseRelease(Fixture(version), new Version(0, 2, 9), Source));

    [Theory]
    [InlineData("\"prerelease\":false", "\"prerelease\":true")]
    [InlineData("\"draft\":false", "\"draft\":true")]
    public void Ignores_unpublished_and_preview_releases(string before, string after) =>
        Assert.Null(UpdateClient.ParseRelease(Fixture().Replace(before, after), new Version(0, 2, 9), Source));

    [Theory]
    [InlineData("0.2.10-preview")]
    [InlineData("0.2.10.1")]
    [InlineData("0.02.10")]
    [InlineData("999999999999.0.0")]
    public void Rejects_ambiguous_or_unsupported_versions(string version) =>
        Assert.Throws<InvalidDataException>(() => UpdateClient.ParseRelease(Fixture(version), new Version(0, 2, 9), Source));

    [Theory]
    [InlineData("https://github.com/LYL-Z/LG-Dsk/releases/download/", "https://evil.example/releases/download/")]
    [InlineData("https://github.com/LYL-Z/LG-Dsk/releases/download/", "http://github.com/LYL-Z/LG-Dsk/releases/download/")]
    [InlineData("\"state\":\"uploaded\"", "\"state\":\"new\"")]
    [InlineData("\"digest\":\"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"", "\"digest\":null")]
    [InlineData("\"size\":4", "\"size\":0")]
    [InlineData("\"size\":4", "\"size\":536870913")]
    [InlineData("-win-x64-Setup.exe", "-win-arm64-Setup.exe")]
    public void Rejects_untrusted_or_incomplete_installers(string before, string after) =>
        Assert.Throws<InvalidDataException>(() => UpdateClient.ParseRelease(Fixture().Replace(before, after), new Version(0, 2, 9), Source));

    [Fact]
    public async Task Download_writes_exact_bytes_and_reports_completion_only_after_validation()
    {
        var bytes = Encoding.UTF8.GetBytes("a real installer fixture");
        var release = Release(bytes);
        using var client = new HttpClient(new FixtureHandler(bytes));
        var updater = new UpdateClient(client, Source);
        var root = TempRoot();
        var percentages = new List<int>();
        try
        {
            var file = await updater.DownloadAsync(release, root, new InlineProgress(percentages), CancellationToken.None);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(file));
            Assert.EndsWith(release.FileName, file);
            Assert.Equal(100, percentages[^1]);
            Assert.Single(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("tamper")]
    [InlineData("oversize")]
    public async Task Invalid_download_never_leaves_an_installable_or_partial_file(string mode)
    {
        var expected = Encoding.UTF8.GetBytes("expected installer");
        var actual = mode switch { "tamper" => Encoding.UTF8.GetBytes("modified installer"), "truncate" => expected[..3], _ => new byte[expected.Length + 1] };
        using var http = new HttpClient(new FixtureHandler(actual, omitLength: true));
        var updater = new UpdateClient(http, Source);
        var root = TempRoot();
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => updater.DownloadAsync(Release(expected), root, null, CancellationToken.None));
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Interrupted_transfer_resumes_from_saved_byte_range()
    {
        var bytes = Encoding.UTF8.GetBytes("a sufficiently long installer fixture for range resume");
        const int interruptedAt = 13;
        using var http = new HttpClient(new InterruptedThenResumedHandler(bytes, interruptedAt));
        var root = TempRoot();
        try
        {
            var updater = new UpdateClient(http, Source);
            var file = await updater.DownloadAsync(Release(bytes), root, null, CancellationToken.None);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(file));
            Assert.Single(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Exhausted_or_cancelled_transfer_retains_partial_for_a_later_retry()
    {
        var bytes = new byte[200000];
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new FixtureHandler(bytes));
        var root = TempRoot();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new UpdateClient(http, Source).DownloadAsync(
                Release(bytes), root, new CancelProgress(cancellation), cancellation.Token));
            Assert.Single(Directory.GetFiles(root, "installer.partial", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Truncated_transfer_is_retried_and_partial_remains_available()
    {
        var expected = Encoding.UTF8.GetBytes("expected installer with a truncated network body");
        using var http = new HttpClient(new FixtureHandler(expected[..7], omitLength: true));
        var root = TempRoot();
        try
        {
            await Assert.ThrowsAsync<IOException>(() => new UpdateClient(http, Source).DownloadAsync(
                Release(expected), root, null, CancellationToken.None));
            Assert.Single(Directory.GetFiles(root, "installer.partial", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Metadata_http_failure_is_not_reported_as_latest_version()
    {
        using var http = new HttpClient(new FixtureHandler([], HttpStatusCode.NotFound));
        await Assert.ThrowsAsync<HttpRequestException>(() => new UpdateClient(http, Source).CheckAsync(new Version(0, 2, 9), CancellationToken.None));
    }

    [Fact]
    public async Task Metadata_transport_drop_is_retried_and_recovers()
    {
        using var http = new HttpClient(new DropOnceMetadataHandler(Fixture()));
        var release = await new UpdateClient(http, Source).CheckAsync(new Version(0, 2, 9), CancellationToken.None);
        Assert.NotNull(release);
    }

    private static UpdateRelease Release(byte[] bytes) => UpdateClient.ParseRelease(
        Fixture(digest: "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)), size: bytes.Length), new Version(0, 2, 9), Source)!;
    private static string TempRoot() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "GlassDesk-update-tests-" + Guid.NewGuid().ToString("N"))).FullName;
    private sealed class InlineProgress(List<int> values) : IProgress<UpdateDownloadProgress> { public void Report(UpdateDownloadProgress value) => values.Add(value.Percent); }
    private sealed class CancelProgress(CancellationTokenSource source) : IProgress<UpdateDownloadProgress> { public void Report(UpdateDownloadProgress value) { if (value.ReceivedBytes > 0) source.Cancel(); } }
    private sealed class FixtureHandler(byte[] bytes, HttpStatusCode status = HttpStatusCode.OK, bool omitLength = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.True(request.Headers.UserAgent.Any());
            return Task.FromResult(new HttpResponseMessage(status) { Content = omitLength ? new UnknownLengthContent(bytes) : new ByteArrayContent(bytes) });
        }
    }

    private sealed class DropOnceMetadataHandler(string json) : HttpMessageHandler
    {
        private int _requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _requests) == 1)
                throw new HttpRequestException("remote host closed the connection");
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/vnd.github+json"),
            };
            return Task.FromResult(response);
        }
    }
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class InterruptedThenResumedHandler(byte[] bytes, int interruptedAt) : HttpMessageHandler
    {
        private int _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.True(request.Headers.UserAgent.Any());
            if (Interlocked.Increment(ref _requestCount) == 1)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new UnknownLengthContent(bytes[..interruptedAt]),
                    Headers = { ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse("\"release-1\"") },
                });

            Assert.Equal(interruptedAt, request.Headers.Range?.Ranges.Single().From);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(bytes[interruptedAt..]),
            };
            response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(
                interruptedAt, bytes.Length - 1, bytes.Length);
            response.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse("\"release-1\"");
            return Task.FromResult(response);
        }
    }
}
