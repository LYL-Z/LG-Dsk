using System.Reflection;

namespace GlassDesk.Dock.Tests;

public sealed class DockForegroundPresentationTests
{
    [Fact]
    public void FullDockRemainsVisibleOnDesktop()
        => AssertState(false, DockPresentationMode.DesktopDock, AutoHideState.Shown);

    [Fact]
    public void OrdinaryForegroundApplicationKeepsDockVisibleWhenAutoHideIsOff()
        => AssertState(false, DockPresentationMode.DesktopDock, AutoHideState.Shown);

    [Fact]
    public void ExplicitSwitcherRequestShowsOnlyCompactSwitcher()
        => AssertState(false, DockPresentationMode.RunningAppSwitcher, AutoHideState.Shown);

    [Fact]
    public void FullscreenApplicationAlwaysHidesDock()
        => AssertState(true, DockPresentationMode.Hidden, AutoHideState.Hidden);

    [Fact]
    public void SettingsWindowHidesDockEvenWhenNoExternalApplicationIsForeground()
    {
        _ = DockPresentationPolicy.EffectiveExternalForeground(externalApplicationForeground: false,
            settingsWindowVisible: true);
        Assert.Equal(DockPresentationMode.Hidden,
            DockPresentationPolicy.Resolve(new DockPresentationContext(
                DockForegroundKind.SecureDesktop,
                DockTaskbarKind.Visible,
                DockFullscreenKind.NotExclusive,
                DockAwarenessRequest.None,
                HasSwitcherContent: false,
                LauncherActive: true,
                SettingsWindowVisible: true)));
        AssertState(false, DockPresentationMode.Hidden, AutoHideState.Hidden);
    }

    [Fact]
    public void UnsafeShellEnvironmentKeepsTheDockConcealed()
        => AssertState(false, DockPresentationMode.Hidden, AutoHideState.Hidden,
            environmentSuppressed: true);

    private static void AssertState(
        bool fullscreen,
        DockPresentationMode mode,
        AutoHideState expected,
        bool environmentSuppressed = false)
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
                update.Invoke(window, [DateTime.UtcNow, fullscreen, mode, environmentSuppressed]);
                Assert.Equal(expected, controller.State);
            }
            finally { window.Close(); }
        });
    }
}
