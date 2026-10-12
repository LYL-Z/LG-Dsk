using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.Settings;
using GlassDesk.UI.Settings;

namespace GlassDesk.Dock.Tests;

public class DockSettingsWindowTests
{
    [Fact]
    public void DockSizeControlsAreRemovedAndLegacyMetricsAreLockedOnTheNextChange()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-dock-settings-" + Guid.NewGuid().ToString("N"));
            try
            {
                var data = new GlassDeskDataStore(new AppPaths(root));
                data.LoadAll();
                data.Settings.Current.Dock.IconSize = 123.4;
                data.Settings.Current.Dock.MagnificationPeak = 1.333;
                data.Settings.Save();
                var host = DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher);
                host.Update(data.Settings.Current.Dock);
                var window = new SettingsWindow();
                window.AttachDataStore(data, null);
                Assert.Null(window.FindName("DockIconSizeSlider"));
                Assert.Null(window.FindName("DockSpacingSlider"));
                Assert.Null(window.FindName("DockPeakSlider"));
                Assert.Null(window.FindName("DockCornerSlider"));
                Assert.IsType<Slider>(window.FindName("DockOffsetXSlider")).Value = 25;

                var reloaded = new SettingsStore(data.Paths.SettingsFile);
                reloaded.Load();
                Assert.Equal(DockSettings.FixedIconSize, reloaded.Current.Dock.IconSize);
                Assert.Equal(DockSettings.FixedIconSpacing, reloaded.Current.Dock.IconSpacing);
                Assert.Equal(DockSettings.FixedMagnificationPeak, reloaded.Current.Dock.MagnificationPeak);
                Assert.Equal(DockSettings.FixedIconSize, host.Settings.IconSize);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
    }

    [Fact]
    public void DockSizeStaysFixedWhilePlacementAndVisibilitySettingsStillApply()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-dock-settings-" + Guid.NewGuid().ToString("N"));
            try
            {
                var data = new GlassDeskDataStore(new AppPaths(root));
                data.LoadAll();
                var host = DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher);
                host.Update(data.Settings.Current.Dock);
                var window = new SettingsWindow();
                window.AttachDataStore(data, null);

                var notifications = 0;
                host.Changed += (_, _) => notifications++;
                Assert.Null(window.FindName("DockIconSizeSlider"));
                Assert.Null(window.FindName("DockSpacingSlider"));
                Assert.Null(window.FindName("DockPeakSlider"));
                Assert.Null(window.FindName("DockCornerSlider"));

                Assert.IsType<ComboBox>(window.FindName("DockPlacementCombo")).SelectedIndex = 3;
                Assert.IsType<Slider>(window.FindName("DockOffsetXSlider")).Value = 25;
                foreach (var removed in new[] { "DockAutoHideCheck", "DockEdgeRevealCheck", "LauncherFullscreenCheck",
                    "HotCornersEnabledCheck", "HotCornerCombo", "HotCornerDwellBox", "MouseSideButtonsEnabledCheck",
                    "MouseBackActionCombo", "MouseForwardActionCombo" })
                    Assert.Null(window.FindName(removed));

                Assert.Equal(DockSettings.FixedIconSize, data.Settings.Current.Dock.IconSize);
                Assert.Equal(DockSettings.FixedIconSpacing, data.Settings.Current.Dock.IconSpacing);
                Assert.Equal(DockSettings.FixedMagnificationPeak, data.Settings.Current.Dock.MagnificationPeak);
                Assert.Equal(DockSettings.FixedIconSize, host.Settings.IconSize);
                Assert.Equal(DockPlacement.TopCenter, host.Settings.Placement);
                Assert.Equal(DockSettings.FixedMagnificationPeak, host.Settings.MagnificationPeak);
                Assert.Equal(25, host.Settings.OffsetX);
                Assert.False(host.Settings.AutoHide);
                Assert.Equal(40, data.Settings.Current.Appearance.DockCornerRadius);
                Assert.True(notifications >= 2);
                var reloaded = new SettingsStore(data.Paths.SettingsFile);
                reloaded.Load();
                Assert.Equal(DockSettings.FixedIconSize, reloaded.Current.Dock.IconSize);
                Assert.Equal(DockSettings.FixedIconSpacing, reloaded.Current.Dock.IconSpacing);
                Assert.Equal(DockPlacement.TopCenter, reloaded.Current.Dock.Placement);
                Assert.Equal(DockSettings.FixedMagnificationPeak, reloaded.Current.Dock.MagnificationPeak);
                Assert.Equal(40, reloaded.Current.Appearance.DockCornerRadius);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
    }
}
