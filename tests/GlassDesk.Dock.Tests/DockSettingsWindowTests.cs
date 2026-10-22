using System.IO;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.Platform.Installation;
using GlassDesk.Settings;
using GlassDesk.UI.Settings;

namespace GlassDesk.Dock.Tests;

public class DockSettingsWindowTests
{
    [Fact]
    public void RefreshInstallationStatusReplacesStaleMismatchAndEnablesUninstall()
    {
        StaTest.Run(() =>
        {
            var window = new SettingsWindow();
            try
            {
                window.AttachInstallation(new InstallationInfo(
                    InstallationState.RegistrationMismatch,
                    Error: "旧的登记状态"));

                var calls = 0;
                var refreshed = new InstallationInfo(
                    InstallationState.VerifiedInstalled,
                    @"D:\GlassDesk",
                    @"D:\GlassDesk\unins000.exe");
                var refreshMethod = typeof(SettingsWindow).GetMethod(
                    "RefreshInstallationStatus",
                    BindingFlags.Instance | BindingFlags.Public,
                    [typeof(Func<InstallationInfo>)]);
                Assert.NotNull(refreshMethod);

                refreshMethod.Invoke(window, [new Func<InstallationInfo>(() =>
                {
                    calls++;
                    return refreshed;
                })]);

                Assert.Equal(1, calls);
                Assert.True(Assert.IsType<Button>(window.FindName("UninstallButton")).IsEnabled);
                Assert.Contains(@"D:\GlassDesk\unins000.exe",
                    Assert.IsType<TextBlock>(window.FindName("InstallationStatus")).Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void DockMetricControlsPersistAndSurviveUnrelatedChanges()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-dock-settings-" + Guid.NewGuid().ToString("N"));
            try
            {
                var data = new GlassDeskDataStore(new AppPaths(root));
                data.LoadAll();
                data.Settings.Current.Dock.IconSize = 56;
                data.Settings.Current.Dock.IconSpacing = 12;
                data.Settings.Current.Dock.MagnificationPeak = 1.6;
                data.Settings.Save();
                var host = DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher);
                host.Update(data.Settings.Current.Dock);
                var window = new SettingsWindow();
                window.AttachDataStore(data, null);
                Assert.Equal(56, Assert.IsType<Slider>(window.FindName("DockIconSizeSlider")).Value);
                Assert.Equal(12, Assert.IsType<Slider>(window.FindName("DockSpacingSlider")).Value);
                Assert.Equal(1.6, Assert.IsType<Slider>(window.FindName("DockPeakSlider")).Value);
                Assert.Null(window.FindName("DockCornerSlider"));
                Assert.IsType<Slider>(window.FindName("DockOffsetXSlider")).Value = 25;

                var reloaded = new SettingsStore(data.Paths.SettingsFile);
                reloaded.Load();
                Assert.Equal(56, reloaded.Current.Dock.IconSize);
                Assert.Equal(12, reloaded.Current.Dock.IconSpacing);
                Assert.Equal(1.6, reloaded.Current.Dock.MagnificationPeak);
                Assert.Equal(56, host.Settings.IconSize);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
    }

    [Fact]
    public void DockMetricsVisibilityAndAutoHideSettingsApplyImmediately()
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
                Assert.NotNull(window.FindName("DockIconSizeSlider"));
                Assert.NotNull(window.FindName("DockSpacingSlider"));
                Assert.NotNull(window.FindName("DockPeakSlider"));
                Assert.Null(window.FindName("DockCornerSlider"));

                Assert.IsType<Slider>(window.FindName("DockIconSizeSlider")).Value = 52;
                Assert.IsType<Slider>(window.FindName("DockSpacingSlider")).Value = 10;
                Assert.IsType<Slider>(window.FindName("DockPeakSlider")).Value = 1.5;
                Assert.IsType<CheckBox>(window.FindName("DockAutoHideCheck")).IsChecked = true;
                Assert.IsType<CheckBox>(window.FindName("ShowRecentApplicationsCheckBox")).IsChecked = false;
                Assert.IsType<ComboBox>(window.FindName("DockPlacementCombo")).SelectedIndex = 3;
                Assert.IsType<Slider>(window.FindName("DockOffsetXSlider")).Value = 25;
                Assert.True(Assert.IsType<CheckBox>(window.FindName("DockEdgeRevealCheck")).IsChecked);
                foreach (var removed in new[] { "LauncherFullscreenCheck",
                    "HotCornersEnabledCheck", "HotCornerCombo", "HotCornerDwellBox", "MouseSideButtonsEnabledCheck",
                    "MouseBackActionCombo", "MouseForwardActionCombo" })
                    Assert.Null(window.FindName(removed));

                Assert.Equal(52, data.Settings.Current.Dock.IconSize);
                Assert.Equal(10, data.Settings.Current.Dock.IconSpacing);
                Assert.Equal(1.5, data.Settings.Current.Dock.MagnificationPeak);
                Assert.False(data.Settings.Current.Dock.ShowRecentApplications);
                Assert.Equal(52, host.Settings.IconSize);
                Assert.Equal(DockPlacement.TopCenter, host.Settings.Placement);
                Assert.Equal(1.5, host.Settings.MagnificationPeak);
                Assert.Equal(25, host.Settings.OffsetX);
                Assert.True(host.Settings.AutoHide);
                Assert.True(host.Settings.EdgeRevealEnabled);
                Assert.Equal(40, data.Settings.Current.Appearance.DockCornerRadius);
                Assert.True(notifications >= 2);
                var reloaded = new SettingsStore(data.Paths.SettingsFile);
                reloaded.Load();
                Assert.Equal(52, reloaded.Current.Dock.IconSize);
                Assert.Equal(10, reloaded.Current.Dock.IconSpacing);
                Assert.Equal(DockPlacement.TopCenter, reloaded.Current.Dock.Placement);
                Assert.Equal(1.5, reloaded.Current.Dock.MagnificationPeak);
                Assert.True(reloaded.Current.Dock.AutoHide);
                Assert.True(reloaded.Current.Dock.EdgeRevealEnabled);
                Assert.Equal(40, reloaded.Current.Appearance.DockCornerRadius);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
    }
}
