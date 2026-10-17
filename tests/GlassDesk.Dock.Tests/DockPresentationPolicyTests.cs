using GlassDesk.Dock;
using GlassDesk.Platform.Shell;

namespace GlassDesk.Dock.Tests;

public sealed class DockPresentationPolicyTests
{
    [Theory]
    [InlineData(false, false, false, false, false, DockPresentationMode.DesktopDock)]
    [InlineData(true, true, false, true, true, DockPresentationMode.DesktopDock)]
    [InlineData(false, true, false, true, false, DockPresentationMode.Hidden)]
    [InlineData(false, true, true, true, true, DockPresentationMode.Hidden)]
    [InlineData(false, true, false, false, true, DockPresentationMode.Hidden)]
    [InlineData(false, true, false, true, true, DockPresentationMode.RunningAppSwitcher)]
    public void ChoosesDockSwitcherOrHiddenState(
        bool launcherActive,
        bool externalForeground,
        bool fullscreen,
        bool hasApps,
        bool switcherRequested,
        DockPresentationMode expected)
    {
        Assert.Equal(expected, DockPresentationPolicy.Resolve(
            launcherActive, externalForeground, fullscreen, hasApps, switcherRequested));
    }

    [Fact]
    public void SwitcherGroupsWindowsByApplicationAndOmitsItsOwnWindows()
    {
        var windows = new[]
        {
            new WindowIdentityInfo((nint)1, "编辑器（最小化）", 10, true, @"C:\Apps\editor.exe", null, null),
            new WindowIdentityInfo((nint)2, "项目 A", 10, false, @"C:\Apps\editor.exe", null, null),
            new WindowIdentityInfo((nint)3, "网页", 20, false, @"C:\Apps\browser.exe", null, null),
            new WindowIdentityInfo((nint)4, "LG Dsk", 99, false, @"C:\Apps\LGDsk.exe", null, null),
        };

        var items = RunningWindowSwitcherItems.Create(windows, ownProcessId: 99);

        Assert.Equal(2, items.Count);
        Assert.Equal((nint)2, items[0].Handle);
        Assert.Equal("editor", items[0].DisplayName);
        Assert.Contains("项目 A", items[0].ToolTipText);
        Assert.DoesNotContain(items, item => item.ProcessId == 99);
    }
}
