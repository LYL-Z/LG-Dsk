using GlassDesk.Dock;
using GlassDesk.Platform.Shell;

namespace GlassDesk.Dock.Tests;

public sealed class DockPresentationPolicyTests
{
    [Theory]
    [InlineData(false, false, false, false, false, DockPresentationMode.DesktopDock)]
    [InlineData(true, true, false, true, true, DockPresentationMode.DesktopDock)]
    [InlineData(false, true, false, true, false, DockPresentationMode.Hidden)]
    [InlineData(false, true, true, true, true, DockPresentationMode.RunningAppSwitcher)]
    [InlineData(false, true, false, false, true, DockPresentationMode.RunningAppSwitcher)]
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

    [Fact]
    public void AutoHideAppIslandCanBeAdmittedForAnExternalWindowThatIsNotGeometricallyFullscreen()
    {
        var mode = DockPresentationPolicy.Resolve(
            launcherActive: false,
            externalApplicationForeground: true,
            foregroundIsFullscreen: false,
            hasSwitchableApplications: false,
            switcherRequested: false,
            appIslandRequested: true);

        Assert.Equal(DockPresentationMode.RunningAppIsland, mode);
    }

    [Fact]
    public void ExplicitSwitcherTakesPrecedenceOverCompactAppIsland()
    {
        var mode = DockPresentationPolicy.Resolve(
            launcherActive: false,
            externalApplicationForeground: true,
            foregroundIsFullscreen: true,
            hasSwitchableApplications: false,
            switcherRequested: true,
            appIslandRequested: true);

        Assert.Equal(DockPresentationMode.RunningAppSwitcher, mode);
    }

    [Fact]
    public void AppIslandListsKnownBackgroundProcessesButExcludesOwnUnknownAndWindowedProcesses()
    {
        var entries = new[]
        {
            new GlassDesk.Core.Models.AppEntry
            {
                Id = "music", DisplayName = "Music", Kind = GlassDesk.Core.Models.AppItemKind.Program,
                Target = @"C:\Apps\Music.exe", IconSource = @"C:\Apps\Music.exe",
            },
            new GlassDesk.Core.Models.AppEntry
            {
                Id = "browser", DisplayName = "Browser", Kind = GlassDesk.Core.Models.AppItemKind.Program,
                Target = @"C:\Apps\Browser.exe",
            },
        };
        var windows = new[]
        {
            new WindowIdentityInfo((nint)10, "Browser", 200, false, @"C:\Apps\Browser.exe", null, null),
        };
        var processes = new[]
        {
            new RunningProcessImage(100, @"C:\Apps\LGDsk.exe"),
            new RunningProcessImage(101, @"C:\Apps\Music.exe"),
            new RunningProcessImage(102, @"C:\Apps\Music.exe"),
            new RunningProcessImage(200, @"C:\Apps\Browser.exe"),
            new RunningProcessImage(300, @"C:\Windows\System32\background.exe"),
        };

        var items = RunningBackgroundAppItems.Create(entries, windows, processes, ownProcessId: 100);

        var item = Assert.Single(items);
        Assert.Equal("Music", item.DisplayName);
        Assert.Equal(2, item.ProcessCount);
        Assert.Contains("后台运行", item.ToolTipText);
    }

    [Fact]
    public void PinnedAppsCanLaunchButBackgroundAndTrayInventoryIsReadOnly()
    {
        var pinned = new RunningAppTile(0, 0, "Browser", "Browser", "B", null, false,
            EntryId: "browser", IsPinnedApp: true);
        var background = new RunningAppTile(0, 101, "Music", "Music background", "M", null, false,
            IsBackgroundOnly: true, BackgroundProcessCount: 1);
        var tray = new RunningAppTile(0, 0, "Sync", "Sync tray icon", "S", null, false,
            IsTrayApp: true);

        Assert.True(pinned.IsWindowSwitchable);
        Assert.False(background.IsWindowSwitchable);
        Assert.False(tray.IsWindowSwitchable);
    }
}
