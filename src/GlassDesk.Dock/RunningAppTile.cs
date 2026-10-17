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
    int BackgroundProcessCount = 0)
{
    public bool IsWindowSwitchable => !IsBackgroundOnly && Handle != 0;
    public string StatusLabel => IsBackgroundOnly
        ? BackgroundProcessCount > 1 ? $"后台 ×{BackgroundProcessCount}" : "后台"
        : IsMinimized ? "最小化" : "运行中";
}
