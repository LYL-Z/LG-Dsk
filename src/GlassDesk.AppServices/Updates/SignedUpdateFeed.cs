using System.Security.Cryptography;
using System.Text.Json;

namespace GlassDesk.AppServices.Updates;

/// <summary>Authenticates publisher metadata independently of the download CDN.</summary>
public static class SignedUpdateFeed
{
    public const int MaximumChunkSize = 16 * 1024 * 1024;
    public static readonly Uri ProductionSource = new("https://updates.chronokalamos.com/");

    public static UpdateRelease? Parse(string envelope, Version current, Uri source,
        string publicKey, DateTimeOffset now)
    {
        try
        {
            ValidateSource(source);
            using var signed = JsonDocument.Parse(envelope);
            var payload = Convert.FromBase64String(signed.RootElement.GetProperty("payload").GetString()!);
            var signature = Convert.FromBase64String(signed.RootElement.GetProperty("signature").GetString()!);
            if (payload.Length > 256 * 1024) throw new InvalidDataException("更新目录过大。");
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKey);
            if (!rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new InvalidDataException("更新目录签名无效。");
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 1
                || root.GetProperty("channel").GetString() != "stable")
                throw new InvalidDataException("不支持的更新目录版本或通道。");
            var published = root.GetProperty("publishedUtc").GetDateTimeOffset();
            var expires = root.GetProperty("expiresUtc").GetDateTimeOffset();
            if (published > now.AddHours(24) || expires <= now || expires <= published
                || expires - published > TimeSpan.FromDays(100))
                throw new InvalidDataException("更新目录已过期或系统时间异常，请使用备用源。");
            var version = Version.Parse(root.GetProperty("version").GetString()!);
            var name = root.GetProperty("fileName").GetString() ?? throw new InvalidDataException("安装包名称缺失。");
            var hash = root.GetProperty("sha256").GetString() ?? throw new InvalidDataException("安装包摘要缺失。");
            var size = root.GetProperty("size").GetInt64();
            var release = new UpdateRelease(version, name,
                new Uri($"https://github.com/LYL-Z/LG-Dsk/releases/download/v{version}/{name}"),
                hash, size, root.GetProperty("notes").GetString() ?? "",
                new Uri($"https://github.com/LYL-Z/LG-Dsk/releases/tag/v{version}"));
            UpdateClient.ValidateInstaller(release);
            var chunks = root.GetProperty("chunks").EnumerateArray().Select(c =>
                new UpdateChunk(c.GetProperty("sha256").GetString() ?? "", c.GetProperty("size").GetInt64())).ToArray();
            ValidateChunks(chunks, size);
            if (release.Notes.Length > 12000) release = release with { Notes = release.Notes[..12000] };
            return version > new Version(current.Major, current.Minor, Math.Max(0, current.Build))
                ? release with { ChunkSource = source, Chunks = Array.AsReadOnly(chunks) } : null;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException
            or KeyNotFoundException or InvalidOperationException or CryptographicException or OverflowException)
        {
            throw new InvalidDataException("签名更新目录格式或签名无效。", ex);
        }
    }

    internal static void ValidateSource(Uri source)
    {
        if (source != ProductionSource)
            throw new InvalidDataException("不受信任的更新文件源。");
    }

    internal static void ValidateChunks(IReadOnlyList<UpdateChunk> chunks, long size)
    {
        if (chunks.Count is < 1 or > 64 || chunks.Any(c => c is null || c.Size is <= 0 or > MaximumChunkSize
            || c.Sha256 is null || c.Sha256.Length != 64 || c.Sha256.Any(x => !char.IsAsciiHexDigit(x)))
            || chunks.GroupBy(c => c.Sha256, StringComparer.OrdinalIgnoreCase).Any(g => g.Select(c => c.Size).Distinct().Count() != 1)
            || chunks.Sum(c => c.Size) != size)
            throw new InvalidDataException("更新分块目录无效。");
    }

    internal static string ReadPublisherKey()
    {
        using var stream = typeof(SignedUpdateFeed).Assembly.GetManifestResourceStream(
            "GlassDesk.AppServices.Resources.update-publisher-public.pem")
            ?? throw new InvalidDataException("发布签名公钥缺失。");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
