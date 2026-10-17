using System.Windows.Media.Imaging;

namespace GlassDesk.Dock;

public sealed record RunningAppTile(
    nint Handle,
    uint ProcessId,
    string DisplayName,
    string ToolTipText,
    string Glyph,
    BitmapSource? Icon,
    bool IsMinimized);
