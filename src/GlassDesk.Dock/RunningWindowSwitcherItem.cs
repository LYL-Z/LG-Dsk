using GlassDesk.Platform.Shell;
using System.IO;

namespace GlassDesk.Dock;

public sealed record RunningWindowSwitcherItem(
    nint Handle,
    uint ProcessId,
    string DisplayName,
    string ToolTipText,
    string? IconSource,
    bool IsMinimized);

public static class RunningWindowSwitcherItems
{
    public static IReadOnlyList<RunningWindowSwitcherItem> Create(
        IEnumerable<WindowIdentityInfo> windows,
        uint ownProcessId)
    {
        ArgumentNullException.ThrowIfNull(windows);

        var candidates = windows
            .Where(window => window.Handle != 0 && window.ProcessId != 0 && window.ProcessId != ownProcessId)
            .ToArray();

        var groups = candidates
            .GroupBy(WindowIdentity, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var result = new List<RunningWindowSwitcherItem>(groups.Length);

        foreach (var group in groups)
        {
            var sameApplication = group.ToArray();

            // EnumWindows returns top-level windows in z-order. Prefer the first
            // non-minimized member so an app click restores the visible window.
            var target = sameApplication.FirstOrDefault(candidate => !candidate.IsMinimized) ?? sameApplication[0];
            var displayName = Path.GetFileNameWithoutExtension(target.ImagePath ?? string.Empty);
            if (string.IsNullOrWhiteSpace(displayName)) displayName = target.Title;
            if (string.IsNullOrWhiteSpace(displayName)) displayName = "应用";

            var titles = sameApplication
                .Select(candidate => candidate.Title.Trim())
                .Where(title => title.Length > 0)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .Take(3)
                .ToArray();
            var tooltip = titles.Length == 0
                ? displayName
                : titles.Length < sameApplication.Length
                    ? $"{displayName}：{string.Join("、", titles)} 等 {sameApplication.Length} 个窗口"
                    : $"{displayName}：{string.Join("、", titles)}";

            result.Add(new RunningWindowSwitcherItem(
                target.Handle,
                target.ProcessId,
                displayName,
                tooltip,
                target.ImagePath,
                target.IsMinimized));
        }

        return result;
    }

    private static string? FirstNonEmpty(string? preferred, string? fallback)
        => !string.IsNullOrWhiteSpace(preferred) ? preferred : !string.IsNullOrWhiteSpace(fallback) ? fallback : null;

    private static string WindowIdentity(WindowIdentityInfo window)
        => FirstNonEmpty(window.EffectiveAumid, window.ImagePath) ?? $"pid:{window.ProcessId}";
}
