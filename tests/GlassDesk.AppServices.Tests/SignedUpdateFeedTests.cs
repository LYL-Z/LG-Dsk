using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GlassDesk.AppServices.Updates;

namespace GlassDesk.AppServices.Tests;

public sealed class SignedUpdateFeedTests
{
    private static readonly Uri GitHub = new("https://api.github.com/repos/LYL-Z/LG-Dsk/releases/latest");
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static Dictionary<string, object?> Payload() => new()
    {
        ["schemaVersion"] = 1, ["channel"] = "stable", ["version"] = "1.6.0",
        ["fileName"] = "LG-Dsk-1.6.0-win-x64-Setup.exe", ["sha256"] = Hash([1, 2]), ["size"] = 2,
        ["publishedUtc"] = Now, ["expiresUtc"] = Now.AddDays(90), ["notes"] = "Release notes",
        ["chunks"] = new[] { new { sha256 = Hash([1, 2]), size = 2 } }
    };

    private static string Sign(Dictionary<string, object?> payload, RSA rsa)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        return JsonSerializer.Serialize(new { payload = Convert.ToBase64String(bytes), signature = Convert.ToBase64String(
            rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) });
    }

    [Fact]
    public void Authenticated_newer_release_is_parsed_and_equal_version_is_ignored()
    {
        using var rsa = RSA.Create(2048);
        var feed = Sign(Payload(), rsa);
        var release = SignedUpdateFeed.Parse(feed, new Version(1, 5, 0), SignedUpdateFeed.ProductionSource, rsa.ExportSubjectPublicKeyInfoPem(), Now);
        Assert.NotNull(release);
        Assert.Equal(SignedUpdateFeed.ProductionSource, release.ChunkSource);
        Assert.Single(release.Chunks);
        Assert.Null(SignedUpdateFeed.Parse(feed, new Version(1, 6, 0), SignedUpdateFeed.ProductionSource, rsa.ExportSubjectPublicKeyInfoPem(), Now));
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("channel")]
    [InlineData("size")]
    [InlineData("nullhash")]
    [InlineData("nullname")]
    [InlineData("chunk")]
    public void Invalid_signed_metadata_is_rejected(string mode)
    {
        using var rsa = RSA.Create(2048);
        var payload = Payload();
        switch (mode)
        {
            case "expired": payload["expiresUtc"] = Now.AddMinutes(-1); break;
            case "channel": payload["channel"] = "candidate"; break;
            case "size": payload["size"] = 3; break;
            case "nullhash": payload["sha256"] = null; break;
            case "nullname": payload["fileName"] = null; break;
            case "chunk": payload["chunks"] = new[] { new { sha256 = "bad", size = 2 } }; break;
        }
        Assert.Throws<InvalidDataException>(() => SignedUpdateFeed.Parse(Sign(payload, rsa), new Version(1, 5, 0),
            SignedUpdateFeed.ProductionSource, rsa.ExportSubjectPublicKeyInfoPem(), Now));
    }

    [Fact]
    public void Wrong_signing_key_and_wrong_source_are_rejected()
    {
        using var rsa = RSA.Create(2048);
        using var other = RSA.Create(2048);
        Assert.Throws<InvalidDataException>(() => SignedUpdateFeed.Parse(Sign(Payload(), rsa), new Version(1, 5, 0),
            SignedUpdateFeed.ProductionSource, other.ExportSubjectPublicKeyInfoPem(), Now));
        Assert.Throws<InvalidDataException>(() => SignedUpdateFeed.Parse(Sign(Payload(), rsa), new Version(1, 5, 0),
            new Uri("https://evil.example/"), rsa.ExportSubjectPublicKeyInfoPem(), Now));
    }

    [Fact]
    public async Task Repeated_content_chunks_download_once_and_reassemble_in_original_order()
    {
        var a = Encoding.UTF8.GetBytes("chunk A");
        var b = Encoding.UTF8.GetBytes("chunk B");
        var expected = a.Concat(b).Concat(a).ToArray();
        var release = new UpdateRelease(new Version(1, 6, 0), "LG-Dsk-1.6.0-win-x64-Setup.exe",
            new Uri("https://github.com/LYL-Z/LG-Dsk/releases/download/v1.6.0/LG-Dsk-1.6.0-win-x64-Setup.exe"),
            Hash(expected), expected.Length, "", new Uri("https://github.com/LYL-Z/LG-Dsk/releases/tag/v1.6.0"))
        {
            ChunkSource = SignedUpdateFeed.ProductionSource,
            Chunks = new[] { new UpdateChunk(Hash(a), a.Length), new UpdateChunk(Hash(b), b.Length), new UpdateChunk(Hash(a), a.Length) }
        };
        var handler = new ChunksHandler(new Dictionary<string, byte[]> { [Hash(a)] = a, [Hash(b)] = b });
        using var http = new HttpClient(handler);
        var root = Path.Combine(Path.GetTempPath(), "LG-Dsk-chunk-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var updater = new UpdateClient(http, GitHub);
            var file = await updater.DownloadAsync(release, root, null, CancellationToken.None);
            Assert.Equal(expected, await File.ReadAllBytesAsync(file));
            Assert.Equal(2, handler.Requests);
            File.Delete(file);
            file = await updater.DownloadAsync(release, root, null, CancellationToken.None);
            Assert.Equal(expected, await File.ReadAllBytesAsync(file));
            Assert.Equal(2, handler.Requests); // Verified chunk cache survives assembly.
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class ChunksHandler(Dictionary<string, byte[]> chunks) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            Assert.Equal(SignedUpdateFeed.ProductionSource.Host, request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(chunks[Path.GetFileNameWithoutExtension(request.RequestUri.AbsolutePath)]) });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Truncated_chunk_resumes_and_corrupt_chunk_restarts(bool corrupt)
    {
        var bytes = Encoding.UTF8.GetBytes("an installer chunk with an interrupted body");
        var release = new UpdateRelease(new Version(1, 6, 0), "LG-Dsk-1.6.0-win-x64-Setup.exe",
            new Uri("https://github.com/LYL-Z/LG-Dsk/releases/download/v1.6.0/LG-Dsk-1.6.0-win-x64-Setup.exe"),
            Hash(bytes), bytes.Length, "", new Uri("https://github.com/LYL-Z/LG-Dsk/releases/tag/v1.6.0"))
        { ChunkSource = SignedUpdateFeed.ProductionSource, Chunks = new[] { new UpdateChunk(Hash(bytes), bytes.Length) } };
        var handler = new InterruptedChunkHandler(bytes, corrupt);
        using var http = new HttpClient(handler);
        var root = Path.Combine(Path.GetTempPath(), "LG-Dsk-resume-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var file = await new UpdateClient(http, GitHub).DownloadAsync(release, root, null, CancellationToken.None);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(file));
            Assert.Equal(2, handler.Requests);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class InterruptedChunkHandler(byte[] bytes, bool corrupt) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref Requests) == 1)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new UnknownLengthContent(corrupt ? new byte[bytes.Length] : bytes[..9]) });
            if (corrupt)
            {
                Assert.Null(request.Headers.Range);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            }
            Assert.Equal(9, request.Headers.Range?.Ranges.Single().From);
            var content = new ByteArrayContent(bytes[9..]);
            content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(9, bytes.Length - 1, bytes.Length);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content });
        }
    }

    [Fact]
    public async Task Rejected_chunk_resume_range_restarts_that_chunk_from_zero()
    {
        var bytes = Encoding.UTF8.GetBytes("chunk with an obsolete cached resume offset");
        var release = new UpdateRelease(new Version(1, 6, 0), "LG-Dsk-1.6.0-win-x64-Setup.exe",
            new Uri("https://github.com/LYL-Z/LG-Dsk/releases/download/v1.6.0/LG-Dsk-1.6.0-win-x64-Setup.exe"),
            Hash(bytes), bytes.Length, "", new Uri("https://github.com/LYL-Z/LG-Dsk/releases/tag/v1.6.0"))
        { ChunkSource = SignedUpdateFeed.ProductionSource, Chunks = new[] { new UpdateChunk(Hash(bytes), bytes.Length) } };
        var handler = new RejectedRangeChunkHandler(bytes);
        using var http = new HttpClient(handler);
        var root = Path.Combine(Path.GetTempPath(), "LG-Dsk-rejected-range-tests-" + Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(root, $"LG-Dsk-{release.Version}-{release.Sha256[..12]}");
        Directory.CreateDirectory(cache);
        await File.WriteAllBytesAsync(Path.Combine(cache, Hash(bytes) + ".chunk.partial"), bytes[..7]);
        try
        {
            var file = await new UpdateClient(http, GitHub).DownloadAsync(release, root, null, CancellationToken.None);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(file));
            Assert.Equal(2, handler.Requests);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class RejectedRangeChunkHandler(byte[] bytes) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(SignedUpdateFeed.ProductionSource.Host, request.RequestUri!.Host);
            if (Interlocked.Increment(ref Requests) == 1)
            {
                Assert.Equal(7, request.Headers.Range?.Ranges.Single().From);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
            }
            Assert.Null(request.Headers.Range);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    [Fact]
    public async Task Unavailable_static_metadata_can_fall_back_to_github_without_claiming_an_update()
    {
        using var http = new HttpClient(new UnavailableStaticHandler());
        Assert.Null(await new UpdateClient(http, GitHub, useStaticFeed: true).CheckAsync(new Version(1, 5, 0), CancellationToken.None));
    }

    private sealed class UnavailableStaticHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(request.RequestUri!.Host == SignedUpdateFeed.ProductionSource.Host
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"draft\":true}") });
    }
}
