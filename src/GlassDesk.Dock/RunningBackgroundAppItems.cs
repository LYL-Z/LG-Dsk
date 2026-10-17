using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using GlassDesk.Core.Models;
using GlassDesk.Platform.Shell;

namespace GlassDesk.Dock;

/// <summary>A process image observed in the current Windows session.</summary>
public sealed record RunningProcessImage(uint ProcessId, string ImagePath);

/// <summary>An application-library entry whose process is alive but has no top-level window.</summary>
public sealed record RunningBackgroundAppItem(
    uint ProcessId,
    string DisplayName,
    string ToolTipText,
    string IconSource,
    int ProcessCount);

/// <summary>Finds known library applications that are running without a switchable window.</summary>
public static class RunningBackgroundAppItems
{
    public static IReadOnlyList<RunningBackgroundAppItem> Create(
        IEnumerable<AppEntry> entries,
        IEnumerable<WindowIdentityInfo> windows,
        IEnumerable<RunningProcessImage> processes,
        uint ownProcessId)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(processes);

        var windowProcessIds = windows
            .Where(window => window.ProcessId != 0 && window.ProcessId != ownProcessId)
            .Select(window => window.ProcessId)
            .ToHashSet();

        var candidates = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!entry.IsUsable || entry.IsTargetMissing
                || entry.Kind is not (AppItemKind.Program or AppItemKind.Shortcut)
                || !TryNormalizeExecutablePath(entry.Target, out var path))
                continue;

            candidates.TryAdd(path, entry);
        }

        var live = processes
            .Where(process => process.ProcessId != 0 && process.ProcessId != ownProcessId
                && !windowProcessIds.Contains(process.ProcessId)
                && TryNormalizeExecutablePath(process.ImagePath, out _))
            .Select(process => new
            {
                process.ProcessId,
                Path = NormalizeExecutablePath(process.ImagePath),
            })
            .GroupBy(process => process.Path, StringComparer.OrdinalIgnoreCase);

        var result = new List<RunningBackgroundAppItem>();
        foreach (var processGroup in live)
        {
            if (!candidates.TryGetValue(processGroup.Key, out var entry)) continue;
            var count = processGroup.Count();
            var name = string.IsNullOrWhiteSpace(entry.DisplayName)
                ? Path.GetFileNameWithoutExtension(processGroup.Key)
                : entry.DisplayName.Trim();
            var label = count == 1 ? "1 个进程" : $"{count} 个进程";
            result.Add(new RunningBackgroundAppItem(
                processGroup.First().ProcessId,
                name,
                $"后台运行 · {label} · 未检测到可切换窗口",
                string.IsNullOrWhiteSpace(entry.IconSource) ? processGroup.Key : entry.IconSource,
                count));
        }

        return result
            .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    /// <summary>Reads process paths only when the island or switcher is visible.</summary>
    public static IReadOnlyList<RunningProcessImage> EnumerateCurrentProcesses()
    {
        var result = new List<RunningProcessImage>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path))
                        result.Add(new RunningProcessImage(unchecked((uint)process.Id), path));
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException
                    or NotSupportedException or UnauthorizedAccessException)
                {
                    // System and elevated processes may deny path inspection. They are simply omitted.
                }
            }
        }

        return result;
    }

    private static bool TryNormalizeExecutablePath(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)
            || !string.Equals(Path.GetExtension(value), ".exe", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            normalized = NormalizeExecutablePath(value);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string NormalizeExecutablePath(string value) => Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar);
}
