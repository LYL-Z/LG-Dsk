using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GlassDesk.AppServices;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.Settings;
using GlassDesk.UI.Settings;

namespace GlassDesk.Dock.Tests;

public sealed class DockBuiltinEntrySettingsTests
{
    [Fact]
    public void AllEntryCombinationsApplyImmediatelyWithoutGapsOrChangingPinnedOrder()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-builtin-" + Guid.NewGuid().ToString("N"));
            var window = new DockWindow();
            try
            {
                var data = new GlassDeskDataStore(new AppPaths(root));
                data.LoadAll();
                using var services = new AppServiceHost(data);
                var host = DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher);
                window.AttachServices(services, host, () => { });
                window.Show();
                var names = new[] { "LauncherButton", "ThisPcButton" };
                var fields = new[] { "ShowLauncherEntry", "ShowThisPcEntry" };
                var buttons = names.Select(n => Assert.IsType<Button>(window.FindName(n))).ToArray();
                var canvas = Assert.IsType<Canvas>(buttons[0].Parent);
                var allButtons = canvas.Children.OfType<Button>().ToArray();
                Assert.Equal(new[] { "FinderButton", "SettingsButton", "LauncherButton", "ThisPcButton" },
                    allButtons.Select(button => button.Name).ToArray());
                Assert.Null(window.FindName("DesktopIconsButton"));
                Assert.Null(window.FindName("DownloadsRecycleDivider"));
                var leadingDivider = Assert.IsType<Border>(window.FindName("LeadingGroupDivider"));
                Assert.IsType<Border>(window.FindName("TrailingGroupDivider"));
                Assert.Equal(Visibility.Collapsed, leadingDivider.Visibility);
                for (var mask = 0; mask < 4; mask++)
                {
                    var settings = new DockSettings();
                    for (var i = 0; i < 2; i++) SetEntry(settings, fields[i], (mask & (1 << i)) != 0);
                    host.Update(settings);
                    window.UpdateLayout();
                    double? previousX = null;
                    for (var i = 0; i < 2; i++)
                    {
                        var visible = (mask & (1 << i)) != 0;
                        Assert.Equal(visible ? Visibility.Visible : Visibility.Collapsed, buttons[i].Visibility);
                        if (!visible) continue;
                        var x = buttons[i].TranslatePoint(new Point(), canvas).X;
                        var restingPitch = 48 + 8;
                        if (previousX is { } prior) Assert.Equal(prior + restingPitch, x, 5);
                        else Assert.True(x >= 2 * restingPitch, "Optional entries follow the permanent Finder and Settings icons.");
                        previousX = x;
                    }
                    Assert.Empty(data.Layouts.Dock.PinnedItemIds);
                    Assert.Equal(0, window.TileCount);
                    if ((mask & 1) == 0) Assert.Null(window.GetLauncherButtonPixelBounds());
                }
            }
            finally { window.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
    }

    [Fact]
    public void SettingsSwitchesPersistAndSurviveOtherDockChangesAndReload()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-builtin-settings-" + Guid.NewGuid().ToString("N"));
            var window = new SettingsWindow();
            try
            {
                var data = new GlassDeskDataStore(new AppPaths(root));
                data.LoadAll();
                var host = DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher);
                window.AttachDataStore(data, null);
                var launcher = Assert.IsType<CheckBox>(window.FindName("ShowLauncherEntryCheckBox"));
                var thisPc = Assert.IsType<CheckBox>(window.FindName("ShowThisPcEntryCheckBox"));
                var recent = Assert.IsType<CheckBox>(window.FindName("ShowRecentApplicationsCheckBox"));
                Assert.True(launcher.IsChecked);
                Assert.False(thisPc.IsChecked);
                Assert.True(recent.IsChecked);
                launcher.IsChecked = false;
                thisPc.IsChecked = true;
                recent.IsChecked = false;
                Assert.IsType<Slider>(window.FindName("DockOffsetXSlider")).Value = 25;
                var reloaded = new SettingsStore(data.Paths.SettingsFile);
                reloaded.Load();
                Assert.False(reloaded.Current.Dock.ShowLauncherEntry);
                Assert.True(reloaded.Current.Dock.ShowThisPcEntry);
                Assert.False(reloaded.Current.Dock.ShowRecentApplications);
                Assert.False(host.Settings.ShowLauncherEntry);
                Assert.True(host.Settings.ShowThisPcEntry);
                Assert.False(host.Settings.ShowRecentApplications);
                Assert.Equal(25, reloaded.Current.Dock.OffsetX);
                var loaded = new GlassDeskDataStore(data.Paths);
                loaded.LoadAll();
                window.AttachDataStore(loaded, null);
                Assert.False(Assert.IsType<CheckBox>(window.FindName("ShowLauncherEntryCheckBox")).IsChecked);
                Assert.True(Assert.IsType<CheckBox>(window.FindName("ShowThisPcEntryCheckBox")).IsChecked);
                Assert.False(Assert.IsType<CheckBox>(window.FindName("ShowRecentApplicationsCheckBox")).IsChecked);
            }
            finally { window.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
    }

    [Fact]
    public void ShortFrameSettingMatchesTheSpaceRequiredByEnabledEntries()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-builtin-frame-" + Guid.NewGuid().ToString("N"));
            var window = new SettingsWindow();
            var dock = new DockWindow();
            try
            {
                var data = new GlassDeskDataStore(new AppPaths(root));
                data.LoadAll();
                using var services = new AppServiceHost(data);
                var host = DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher);
                dock.AttachServices(services, host, () => { });
                window.AttachDataStore(data, null);
                dock.Show();
                var frame = Assert.IsType<Slider>(window.FindName("DockFrameLengthSlider"));
                frame.Value = frame.Minimum + 8;
                frame.Value = frame.Minimum;
                dock.UpdateLayout();
                Assert.Equal(frame.Value, dock.ActualWidth, 5);
                Assert.IsType<CheckBox>(window.FindName("ShowThisPcEntryCheckBox")).IsChecked = true;
                frame.Value = frame.Minimum;
                dock.UpdateLayout();
                Assert.Equal(frame.Value, dock.ActualWidth, 5);
            }
            finally { dock.Close(); window.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
    }

    [Fact]
    public void DesktopIconsToggleIsNotPartOfTheDock()
    {
        StaTest.Run(() =>
        {
            var window = new DockWindow();
            try
            {
                Assert.Null(window.FindName("DesktopIconsButton"));
                Assert.Null(typeof(DockWindow).GetProperty("ToggleDesktopIcons"));
            }
            finally { window.Close(); }
        });
    }

    private static void SetEntry(DockSettings settings, string name, bool value)
    {
        var property = typeof(DockSettings).GetProperty(name);
        Assert.NotNull(property);
        property.SetValue(settings, value);
    }

    private static bool GetEntry(DockSettings settings, string name) =>
        (bool)typeof(DockSettings).GetProperty(name)!.GetValue(settings)!;
}
