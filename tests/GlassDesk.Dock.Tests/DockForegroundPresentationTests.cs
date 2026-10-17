using System.Reflection;

namespace GlassDesk.Dock.Tests;

public sealed class DockForegroundPresentationTests
{
    [Fact]
    public void FullDockRemainsVisibleOnDesktop()
        => AssertState(covered: false, fullscreen: false, DockPresentationMode.DesktopDock, AutoHideState.Shown);

    [Fact]
    public void ForegroundApplicationHidesDockWithoutMouseEdgeReveal()
        => AssertState(covered: true, fullscreen: false, DockPresentationMode.Hidden, AutoHideState.Hidden);

    [Fact]
    public void ExplicitSwitcherRequestShowsOnlyCompactSwitcher()
        => AssertState(covered: true, fullscreen: false, DockPresentationMode.RunningAppSwitcher, AutoHideState.Shown);

    [Fact]
    public void FullscreenApplicationAlwaysHidesDock()
        => AssertState(covered: true, fullscreen: true, DockPresentationMode.Hidden, AutoHideState.Hidden);

    [Fact]
    public void SettingsWindowHidesDockEvenWhenNoExternalApplicationIsForeground()
    {
        var covered = DockPresentationPolicy.EffectiveExternalForeground(externalApplicationForeground: false,
            settingsWindowVisible: true);
        Assert.Equal(DockPresentationMode.Hidden,
            DockPresentationPolicy.Resolve(launcherActive: true, externalApplicationForeground: false,
                foregroundIsFullscreen: false, hasSwitchableApplications: true, switcherRequested: false,
                settingsWindowVisible: true));
        AssertState(covered, fullscreen: false, DockPresentationMode.Hidden, AutoHideState.Hidden);
    }

    private static void AssertState(bool covered, bool fullscreen, DockPresentationMode mode, AutoHideState expected)
    {
        StaTest.Run(() =>
        {
            var window = new DockWindow();
            try
            {
                var controller = new AutoHideController(300);
                typeof(DockWindow).GetField("_autoHide", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(window, controller);
                var update = typeof(DockWindow).GetMethod("UpdateAutoHideState", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(update);
                update.Invoke(window, [DateTime.UtcNow, fullscreen, covered, mode]);
                Assert.Equal(expected, controller.State);
            }
            finally { window.Close(); }
        });
    }
}
