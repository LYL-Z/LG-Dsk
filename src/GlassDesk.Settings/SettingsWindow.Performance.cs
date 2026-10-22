using System.Windows;
using GlassDesk.Core.Models.Settings;

namespace GlassDesk.Settings;

public partial class SettingsWindow
{
    private bool _loadingPerformanceControls = true;
    public event EventHandler? PerformanceSettingsChanged;

    private void LoadPerformanceControls()
    {
        _loadingPerformanceControls = true;
        try
        {
            AutoSuspendWhenFullscreenCheck.IsChecked = _dataStore?.Settings.Current.Performance.AutoSuspendWhenFullscreen ?? true;
            AutoSuspendWhenFullscreenCheck.IsEnabled = _dataStore is not null;
            FullscreenAppIslandCheck.IsChecked = _dataStore?.Settings.Current.Performance.ShowFullscreenAppIsland ?? true;
            FullscreenAppIslandCheck.IsEnabled = _dataStore is not null;
            ExpandIslandOnHoverCheck.IsChecked = _dataStore?.Settings.Current.Performance.ExpandIslandOnHover ?? true;
            ExpandIslandOnHoverCheck.IsEnabled = _dataStore is not null;
            ShowTrayIconsCheck.IsChecked = _dataStore?.Settings.Current.Performance.ShowTrayIcons ?? true;
            ShowTrayIconsCheck.IsEnabled = _dataStore is not null;
        }
        finally { _loadingPerformanceControls = false; }
    }

    private void OnPerformanceControlChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingPerformanceControls || _dataStore is null) return;
        var current = _dataStore.Settings.Current;
        var previous = current.Performance;
        try
        {
            current.Performance = new PerformanceSettings
            {
                AutoSuspendWhenFullscreen = AutoSuspendWhenFullscreenCheck.IsChecked == true,
                ShowFullscreenAppIsland = FullscreenAppIslandCheck.IsChecked == true,
                ExpandIslandOnHover = ExpandIslandOnHoverCheck.IsChecked == true,
                ShowTrayIcons = ShowTrayIconsCheck.IsChecked == true,
            };
            _dataStore.Settings.Save();
        }
        catch (Exception error)
        {
            current.Performance = previous;
            LoadPerformanceControls();
            var islandControl = !ReferenceEquals(sender, AutoSuspendWhenFullscreenCheck);
            StatusFor(islandControl ? "应用岛" : "性能与兼容").Text = "保存失败，已恢复原设置：" + error.Message;
            return;
        }
        PerformanceSettingsChanged?.Invoke(this, EventArgs.Empty);
        var isIslandSetting = !ReferenceEquals(sender, AutoSuspendWhenFullscreenCheck);
        StatusFor(isIslandSetting ? "应用岛" : "性能与兼容").Text = isIslandSetting
            ? "已保存并应用。托盘项目只包含 Windows 辅助功能树公开的项目。"
            : "已保存并应用。全屏时自动休眠仅影响后台扫描与动画，不会修改 Windows 系统设置。";
    }
}
