using GlassDesk.Dock;

namespace GlassDesk.Dock.Tests;

public sealed class ShellPlacementMessageFilterTests
{
    [Fact]
    public void Reconciles_when_windows_work_area_changes()
    {
        Assert.True(ShellPlacementMessageFilter.ShouldReconcile(0x001A, (nint)0x002F));
    }

    [Theory]
    [InlineData(0x001A, 0x0000)]
    [InlineData(0x0312, 0x0000)]
    [InlineData(0x0005, 0x0000)]
    public void Ignores_unrelated_window_messages(int message, int wParam)
    {
        Assert.False(ShellPlacementMessageFilter.ShouldReconcile(message, (nint)wParam));
    }

    [Theory]
    [InlineData(0x007E)]
    [InlineData(0x02E0)]
    public void Reconciles_after_display_geometry_or_dpi_changes(int message)
    {
        Assert.True(ShellPlacementMessageFilter.ShouldReconcile(message, nint.Zero));
    }
}
