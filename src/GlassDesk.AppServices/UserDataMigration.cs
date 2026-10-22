using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using GlassDesk.Core.Storage;
using GlassDesk.Platform.Storage;

namespace GlassDesk.AppServices;

public enum UserDataMigrationStatus
{
    NoLegacyData,
    AlreadyAtDestination,
    Migrated,
    SourceInUse,
    Conflict,
    Failed,
}

public sealed record UserDataMigrationResult(UserDataMigrationStatus Status, int FilesCopied = 0, string? Error = null)
{
    public bool CanContinue => Status is UserDataMigrationStatus.NoLegacyData
        or UserDataMigrationStatus.AlreadyAtDestination or UserDataMigrationStatus.Migrated;
}

/// <summary>Copies legacy program-folder data to the installed user's profile without deleting the source.</summary>
public static class UserDataMigration
{
    private const string CompletionMarkerFileName = ".lg-dsk-legacy-migration.json";
    private static readonly JsonSerializerOptions MarkerJsonOptions = new() { WriteIndented = true };

    public static UserDataMigrationResult MigrateLegacyData(string legacyDirectory, string userDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataDirectory);
        string? stage = null;
        try
        {
            var source = Path.GetFullPath(legacyDirectory);
            var destination = Path.GetFullPath(userDataDirectory);
            if (PathsEqual(source, destination) || DataDirectoryGuard.Overlaps(source, destination))
                return new(UserDataMigrationStatus.Conflict, Error: "旧数据目录与新数据目录重叠，已停止迁移。");
            if (!Directory.Exists(source))
                return new(UserDataMigrationStatus.NoLegacyData);
            if (!Directory.EnumerateFileSystemEntries(source).Any())
                return new(UserDataMigrationStatus.NoLegacyData);
            if (Directory.Exists(destination))
            {
                if (TryReadCompletionMarker(destination, source, out var previousCopyCount))
                    return new(UserDataMigrationStatus.AlreadyAtDestination, previousCopyCount);
                return new(UserDataMigrationStatus.Conflict,
                    Error: "旧安装目录和用户数据目录同时存在。为避免覆盖任一份数据，已停止启动迁移。");
            }
            if (!UserDataLease.TryAcquireExclusive(source, out var lease) || lease is null)
                return new(UserDataMigrationStatus.SourceInUse,
                    Error: "旧版数据仍被另一个 LG Dsk 实例使用。请退出其它实例后再启动新版本。");

            using (lease)
            {
                EnsureNoReparsePoints(source);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                stage = destination + ".migration-" + Guid.NewGuid().ToString("N");
                Directory.CreateDirectory(stage);
                var copied = CopyAndVerify(source, stage);
                WriteCompletionMarker(stage, source, copied);
                if (Directory.Exists(destination))
                    return new(UserDataMigrationStatus.Conflict, copied,
                        "迁移过程中发现目标数据目录已创建；两份数据均保留，未覆盖目标。");
                Directory.Move(stage, destination);
                stage = null;
                return new(UserDataMigrationStatus.Migrated, copied);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
            or ArgumentException or NotSupportedException or CryptographicException or JsonException)
        {
            var stageDescription = stage is null ? string.Empty : $" 暂存副本（如已生成）保留在 {stage}。";
            return new(UserDataMigrationStatus.Failed, Error:
                $"旧数据复制或校验失败。旧目录仍保留；{stageDescription}{Environment.NewLine}{ex.Message}");
        }
    }

    private static void WriteCompletionMarker(string stageRoot, string source, int copied)
    {
        var marker = new MigrationMarker(1, source, copied, DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize(marker, MarkerJsonOptions);
        using var stream = new FileStream(Path.Combine(stageRoot, CompletionMarkerFileName), FileMode.CreateNew,
            FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream);
        writer.Write(json);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static bool TryReadCompletionMarker(string destination, string source, out int copied)
    {
        copied = 0;
        var markerPath = Path.Combine(destination, CompletionMarkerFileName);
        if (!File.Exists(markerPath))
            return false;

        try
        {
            using var stream = File.OpenRead(markerPath);
            var marker = JsonSerializer.Deserialize<MigrationMarker>(stream);
            if (marker is not { SchemaVersion: 1, FilesCopied: >= 0 }
                || !PathsEqual(marker.SourceDirectory, source))
                return false;
            copied = marker.FilesCopied;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or JsonException)
        {
            return false;
        }
    }

    private sealed record MigrationMarker(int SchemaVersion, string SourceDirectory, int FilesCopied,
        DateTimeOffset CompletedUtc);

    private static int CopyAndVerify(string sourceRoot, string stageRoot)
    {
        var pending = new Stack<string>();
        pending.Push(sourceRoot);
        var copied = 0;
        while (pending.Count > 0)
        {
            var currentSource = pending.Pop();
            var relativeDirectory = Path.GetRelativePath(sourceRoot, currentSource);
            var currentDestination = relativeDirectory == "."
                ? stageRoot
                : Path.Combine(stageRoot, relativeDirectory);
            Directory.CreateDirectory(currentDestination);

            foreach (var entry in Directory.EnumerateFileSystemEntries(currentSource))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"迁移目录包含联接或符号链接，已停止：{entry}");
                var target = Path.Combine(currentDestination, Path.GetFileName(entry));
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }

                File.Copy(entry, target, overwrite: false);
                File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(entry));
                if (!HashesEqual(entry, target))
                    throw new IOException($"迁移文件 SHA-256 不一致：{entry}");
                copied++;
            }
        }

        return copied;
    }

    private static void EnsureNoReparsePoints(string root)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("旧数据目录本身是联接或符号链接，已停止迁移。");
    }

    private static bool HashesEqual(string left, string right)
    {
        using var leftStream = File.OpenRead(left);
        using var rightStream = File.OpenRead(right);
        var leftHash = SHA256.HashData(leftStream);
        var rightHash = SHA256.HashData(rightStream);
        return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(left), Path.TrimEndingDirectorySeparator(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
