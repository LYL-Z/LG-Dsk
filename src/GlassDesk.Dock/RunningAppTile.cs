using System.Windows.Media.Imaging;

namespace GlassDesk.Dock;

public sealed record RunningAppTile(
    nint Handle,
    uint ProcessId,
    string DisplayName,
    string ToolTipText,
    string Glyph,
    BitmapSource? Icon,
    bool IsMinimized,
    bool IsBackgroundOnly = false,
    int BackgroundProcessCount = 0,
    string? EntryId = null,
    bool IsPinnedApp = false,
    bool IsTrayApp = false)
{
    public bool IsWindowSwitchable => IsPinnedApp && !string.IsNullOrWhiteSpace(EntryId)
        || !IsBackgroundOnly && !IsTrayApp && Handle != 0;
    public string StatusLabel => IsTrayApp ? "托盘"
        : IsPinnedApp ? "固定"
        : IsBackgroundOnly
        ? BackgroundProcessCount > 1 ? $"后台 ×{BackgroundProcessCount}" : "后台"
        : IsMinimized ? "最小化" : "运行中";
}
