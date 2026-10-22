using System.IO;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Platform.Shell;

namespace GlassDesk.Dock;

internal sealed record FolderStackItem(
    string Path,
    string DisplayName,
    bool IsDirectory,
    DateTime LastModifiedUtc,
    ExtractedIcon? Icon);

internal sealed record FolderStackSnapshot(
    IReadOnlyList<FolderStackItem> Items,
    bool HasMore,
    string? Error)
{
    public const int MaximumItems = 50;
}

internal sealed record FolderStackCandidate(
    string Path,
    string DisplayName,
    bool IsDirectory,
    DateTime LastModifiedUtc);

internal sealed record BoundedFolderItems(IReadOnlyList<FolderStackCandidate> Items, bool HasMore);

/// <summary>Deterministically selects the first K entries for the requested order with O(K) memory.</summary>
internal static class FolderStackSnapshotPolicy
{
    public static BoundedFolderItems SelectItems(
        IEnumerable<FolderStackCandidate> source,
        DockFolderStackSortMode sortMode,
        int maximumItems = FolderStackSnapshot.MaximumItems)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumItems, 1);
        if (!Enum.IsDefined(sortMode)) sortMode = DockFolderStackSortMode.Name;

        var comparer = Comparer<FolderStackCandidate>.Create((left, right) =>
        {
            if (ReferenceEquals(left, right)) return 0;

            var comparison = sortMode == DockFolderStackSortMode.ModifiedDate
                ? right.LastModifiedUtc.CompareTo(left.LastModifiedUtc)
                : StringComparer.CurrentCultureIgnoreCase.Compare(left.DisplayName, right.DisplayName);
            if (comparison == 0)
                comparison = StringComparer.Ordinal.Compare(left.DisplayName, right.DisplayName);
            if (comparison == 0)
                comparison = StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path);
            if (comparison == 0)
                comparison = StringComparer.Ordinal.Compare(left.Path, right.Path);
            return comparison;
        });

        var selected = new SortedSet<FolderStackCandidate>(comparer);
        var hasMore = false;
        foreach (var item in source)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Path)
                || string.IsNullOrWhiteSpace(item.DisplayName))
                continue;

            if (!selected.Add(item)) continue;
            if (selected.Count > maximumItems)
            {
                selected.Remove(selected.Max!);
                hasMore = true;
            }
        }

        return new BoundedFolderItems(selected.ToArray(), hasMore);
    }
}

/// <summary>Reads folder metadata off the WPF dispatcher and extracts icons only for the visible page.</summary>
internal static class FolderStackReader
{
    public static FolderStackSnapshot Read(
        string folderPath,
        CancellationToken cancellationToken,
        DockFolderStackSortMode sortMode = DockFolderStackSortMode.Name)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Path.IsPathFullyQualified(folderPath))
            return new FolderStackSnapshot([], false, "文件夹路径无效；仍可在文件管理器中打开。");

        try
        {
            var fullPath = Path.GetFullPath(folderPath);
            if (!Directory.Exists(fullPath))
                return new FolderStackSnapshot([], false, "文件夹不存在或当前不可访问；可尝试在文件管理器中打开。");

            IEnumerable<FolderStackCandidate> EnumerateCandidates()
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(fullPath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var candidate = ReadCandidate(path, cancellationToken);
                    if (candidate is not null) yield return candidate;
                }
            }

            var bounded = FolderStackSnapshotPolicy.SelectItems(EnumerateCandidates(), sortMode);
            var items = new List<FolderStackItem>(bounded.Items.Count);
            foreach (var candidate in bounded.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                items.Add(new FolderStackItem(
                    candidate.Path,
                    candidate.DisplayName,
                    candidate.IsDirectory,
                    candidate.LastModifiedUtc,
                    ExtractIconSafely(candidate.Path)));
            }

            return new FolderStackSnapshot(items, bounded.HasMore, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return new FolderStackSnapshot([], false, "无法读取文件夹内容；仍可在文件管理器中打开。");
        }
    }

    private static FolderStackCandidate? ReadCandidate(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                // Keep an enumerated path visible even when metadata cannot be queried.
                attributes = 0;
            }

            var name = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(name)) name = path;

            DateTime lastModifiedUtc;
            try
            {
                lastModifiedUtc = File.GetLastWriteTimeUtc(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                // Entries that disappear or deny metadata access remain visible and sort last by date.
                lastModifiedUtc = DateTime.MinValue;
            }

            return new FolderStackCandidate(
                path,
                name,
                (attributes & FileAttributes.Directory) != 0,
                lastModifiedUtc);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or System.Security.SecurityException)
        { return null; }
    }

    private static ExtractedIcon? ExtractIconSafely(string path)
    {
        try
        {
            return IconExtractor.Extract(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or System.Security.SecurityException
            or System.ComponentModel.Win32Exception)
        {
            // A failed icon read must not hide an otherwise valid file from the stack.
            return null;
        }
    }
}
