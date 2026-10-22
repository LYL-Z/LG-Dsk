using System.Windows;
using System.Windows.Automation;

namespace GlassDesk.Settings;

public partial class SettingsWindow
{
    public event EventHandler? CheckForUpdatesRequested;
    public event Action<bool>? AutomaticUpdateCheckChanged;

    private bool _loadingUpdateControls;

    public void LoadUpdateControls()
    {
        _loadingUpdateControls = true;
        try
        {
            AutomaticUpdateCheckBox.IsChecked = _dataStore?.Settings.Current.Updates.CheckAutomatically ?? true;
            AutomaticUpdateCheckBox.IsEnabled = _dataStore is not null;
        }
        finally { _loadingUpdateControls = false; }
    }

    public void ShowUpdateStatus(string version, string status, bool checking = false, string actionText = "检查更新")
    {
        ProductVersionText.Text = "LG Dsk " + version;
        UpdateStatusText.Text = status;
        CheckUpdateButton.IsEnabled = !checking;
        CheckUpdateButton.Content = checking ? "正在检查…" : actionText;
        AutomationProperties.SetName(CheckUpdateButton, checking ? "正在检查 LG Dsk 更新" : actionText + "（LG Dsk）");
    }

    private void OnCheckUpdateClick(object sender, RoutedEventArgs e) =>
        CheckForUpdatesRequested?.Invoke(this, EventArgs.Empty);

    private void OnAutomaticUpdateCheckChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingUpdateControls || _dataStore is null) return;
        var updates = _dataStore.Settings.Current.Updates;
        var previous = updates.CheckAutomatically;
        var enabled = AutomaticUpdateCheckBox.IsChecked == true;
        updates.CheckAutomatically = enabled;
        try { _dataStore.Settings.Save(); }
        catch (Exception error)
        {
            updates.CheckAutomatically = previous;
            LoadUpdateControls();
            UpdateStatusText.Text = "保存更新检查设置失败，已恢复原值：" + error.Message;
            return;
        }

        AutomaticUpdateCheckChanged?.Invoke(enabled);
        UpdateStatusText.Text = enabled
            ? "已启用自动检查。优先检查签名的静态正式版目录，必要时回退到 GitHub；下载与安装前仍会征求确认。"
            : "已关闭自动检查；仍可随时手动检查更新。";
    }
}
