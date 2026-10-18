using GlassDesk.Dock;
using GlassDesk.Platform.Displays;

namespace GlassDesk.Dock.Tests;

public sealed class TaskbarWorkAreaCalculatorTests
{
    [Fact]
    public void Excludes_intersecting_bottom_taskbar_after_ShowDesktop_restores_full_work_area()
    {
        var display = new PixelRect(0, 0, 3840, 2400);
        var restoredWorkArea = display;
        var taskbar = new PixelRect(0, 2340, 3840, 60);

        Assert.Equal(new PixelRect(0, 0, 3840, 2340),
            TaskbarWorkAreaCalculator.ExcludeTaskbar(display, restoredWorkArea, taskbar));
    }

    [Fact]
    public void Leaves_work_area_unchanged_when_taskbar_is_already_excluded()
    {
        var display = new PixelRect(0, 0, 1920, 1080);
        var workArea = new PixelRect(0, 0, 1920, 1040);
        var taskbar = new PixelRect(0, 1040, 1920, 40);

        Assert.Equal(workArea, TaskbarWorkAreaCalculator.ExcludeTaskbar(display, workArea, taskbar));
    }

    [Fact]
    public void Excludes_left_taskbar_from_full_work_area()
    {
        var display = new PixelRect(-1920, 0, 1920, 1080);
        var taskbar = new PixelRect(-1920, 0, 64, 1080);

        Assert.Equal(new PixelRect(-1856, 0, 1856, 1080),
            TaskbarWorkAreaCalculator.ExcludeTaskbar(display, display, taskbar));
    }

    [Fact]
    public void Ignores_non_edge_window_rectangles()
    {
        var display = new PixelRect(0, 0, 1920, 1080);
        var workArea = new PixelRect(0, 0, 1920, 1080);
        var floating = new PixelRect(400, 400, 900, 48);

        Assert.Equal(workArea, TaskbarWorkAreaCalculator.ExcludeTaskbar(display, workArea, floating));
    }
}
