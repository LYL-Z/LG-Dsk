using System.IO;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using GlassDesk.Core.Storage;
using GlassDesk.Dock;
using GlassDesk.Settings;

namespace GlassDesk.Dock.Tests;

public sealed class PerformanceRuntimeWindowTests
{
    [Fact]
    public void NormalHiddenCaptureSleepRetainsPassiveRevealDetection()
    {
        StaTest.Run(() =>
        {
            var window = new DockWindow();
            var captureSleep = typeof(DockWindow).GetMethod("SetBackgroundCaptureSleeping");
            Assert.NotNull(captureSleep);
            captureSleep.Invoke(window, [true]);
            var timer = Assert.IsType<DispatcherTimer>(typeof(DockWindow).GetField("_frameTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
            Assert.True(timer.IsEnabled);
            Assert.Equal(TimeSpan.FromMilliseconds(100), timer.Interval);
            Assert.False((bool)typeof(DockWindow).GetProperty("IsPerformanceSleeping")!.GetValue(window)!);
            window.Close();
        });
    }
    [Fact]
    public void GameSleepStopsDockTimerAndPassiveActionsCannotRestartIt()
    {
        StaTest.Run(() =>
        {
            var window = new DockWindow();
            var sleep = typeof(DockWindow).GetMethod("SetPerformanceSleeping");
            Assert.NotNull(sleep);
            sleep.Invoke(window, [true]);
            window.SetLauncherContainerState(true);
            window.ToggleRevealLock();
            var timer = typeof(DockWindow).GetField("_frameTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) as DispatcherTimer;
            Assert.False(timer?.IsEnabled == true);
            Assert.True((bool)typeof(DockWindow).GetProperty("IsPerformanceSleeping")!.GetValue(window)!);
            Assert.False((bool)typeof(DockWindow).GetProperty("IsPerformanceSurfaceVisible")!.GetValue(window)!);
            sleep.Invoke(window, [false]);
            Assert.False((bool)typeof(DockWindow).GetProperty("IsPerformanceSleeping")!.GetValue(window)!);
            window.Close();
        });
    }

    [Fact]
    public void PerformanceCheckboxPersistsIndependentlyAndNotifiesHost()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-performance-settings-" + Guid.NewGuid().ToString("N"));
            try
            {
                var data = new GlassDeskDataStore(new AppPaths(root));
                data.LoadAll();
                data.Settings.Current.Display.SuppressTriggersWhenFullscreen = false;
                var window = new SettingsWindow();
                window.AttachDataStore(data, null);
                var check = Assert.IsType<CheckBox>(window.FindName("AutoSuspendWhenFullscreenCheck"));
                Assert.True(check.IsChecked);
                var notifications = 0;
                var changed = typeof(SettingsWindow).GetEvent("PerformanceSettingsChanged");
                Assert.NotNull(changed);
                changed.AddEventHandler(window, new EventHandler((_, _) => notifications++));
                check.IsChecked = false;
                var reload = new SettingsStore(data.Paths.SettingsFile);
                reload.Load();
                var performance = typeof(GlassDesk.Core.Models.Settings.GlassDeskSettings).GetProperty("Performance")!.GetValue(reload.Current)!;
                Assert.False((bool)performance.GetType().GetProperty("AutoSuspendWhenFullscreen")!.GetValue(performance)!);
                Assert.False(reload.Current.Display.SuppressTriggersWhenFullscreen);
                Assert.Equal(1, notifications);
                window.Close();
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
    }
}
