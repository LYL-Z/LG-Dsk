namespace GlassDesk.Dock;

/// <summary>Recognizes shell notifications that can invalidate Dock placement.</summary>
internal static class ShellPlacementMessageFilter
{
    private const int WmSettingChange = 0x001A;
    private const int WmDisplayChange = 0x007E;
    private const int WmDpiChanged = 0x02E0;
    private const long SpiSetWorkArea = 0x002F;

    internal static bool ShouldReconcile(int message, nint wParam) => message switch
    {
        WmSettingChange => wParam.ToInt64() == SpiSetWorkArea,
        WmDisplayChange or WmDpiChanged => true,
        _ => false,
    };
}
