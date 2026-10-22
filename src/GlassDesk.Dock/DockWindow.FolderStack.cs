using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GlassDesk.Core.Models;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.Platform;
using GlassDesk.Platform.Shell;
using GlassDesk.UI.Theming;

namespace GlassDesk.Dock;

public partial class DockWindow
{
    private Popup? _folderStackPopup;
    private CancellationTokenSource? _folderStackLoadCancellation;

    /// <summary>Opens a folder entry as a real stack rather than treating it as an application.</summary>
    private void ShowFolderPanel(AppEntry folderEntry)
        => ShowFolderPanel(folderEntry.DisplayName, folderEntry.Target, placementTarget: null);

    private void ShowFolderPanel(string displayName, string folderPath, UIElement? placementTarget)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            ShowFailureNotice("文件夹路径无效。可在应用库中重新定位这个文件夹。");
            return;
        }

        if (_folderStackPopup is { IsOpen: true } existing)
            existing.IsOpen = false;

        var foregroundBefore = WindowControl.GetForegroundWindow();
        var palette = ThemeService.Current?.Palette;
        var panel = new FolderStackPanel(
            displayName,
            Settings.FolderStackViewMode,
            Settings.FolderStackSortMode,
            palette?.ResolvePanelBrush(false) ?? Brushes.DimGray,
            palette?.PanelBorderBrush ?? Brushes.Gray,
            palette?.ForegroundBrush ?? Brushes.White,
            palette?.SecondaryForegroundBrush ?? Brushes.LightGray);
        var popup = new Popup
        {
            Child = panel,
            PlacementTarget = placementTarget,
            Placement = placementTarget is null
                ? PlacementMode.MousePoint
                : Settings.Placement == DockPlacement.TopCenter ? PlacementMode.Bottom : PlacementMode.Top,
            StaysOpen = false,
            AllowsTransparency = true,
        };
        _folderStackPopup = popup;

        panel.ViewModeChanged = PersistFolderStackViewMode;
        panel.SortModeChanged = mode =>
        {
            if (!PersistFolderStackSortMode(mode)) return false;
            _ = RefreshFolderStackAsync(panel, popup, folderPath);
            return true;
        };
        panel.RefreshRequested += () => _ = RefreshFolderStackAsync(panel, popup, folderPath);
        panel.OpenFolderRequested += () => OpenFolderStackPath(folderPath, "文件夹没能打开。");
        panel.ItemInvoked += path => OpenFolderStackPath(path, $"没能打开：{Path.GetFileName(path)}");
        panel.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            popup.IsOpen = false;
            args.Handled = true;
        };
        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_folderStackPopup, popup)) _folderStackPopup = null;
            CancelFolderStackLoad();
            _autoHide?.PopSuppress(DateTime.UtcNow);
            RestorePopupForeground(foregroundBefore, 0);
        };

        _autoHide?.PushSuppress(DateTime.UtcNow);
        _ = WindowActivation.TryBringToForeground(Handle);
        popup.IsOpen = true;
        _ = panel.Focus();
        _ = RefreshFolderStackAsync(panel, popup, folderPath);
    }

    private async Task RefreshFolderStackAsync(FolderStackPanel panel, Popup popup, string folderPath)
    {
        var previous = _folderStackLoadCancellation;
        var current = new CancellationTokenSource();
        _folderStackLoadCancellation = current;
        previous?.Cancel();
        panel.SetLoading();

        try
        {
            var sortMode = Settings.FolderStackSortMode;
            var snapshot = await Task.Run(
                () => FolderStackReader.Read(folderPath, current.Token, sortMode),
                current.Token);
            if (!current.IsCancellationRequested
                && ReferenceEquals(_folderStackLoadCancellation, current)
                && ReferenceEquals(_folderStackPopup, popup)
                && popup.IsOpen)
            {
                panel.SetSnapshot(snapshot);
            }
        }
        catch (OperationCanceledException)
        {
            // Closing the stack or requesting a newer snapshot retires this read quietly.
        }
        finally
        {
            if (ReferenceEquals(_folderStackLoadCancellation, current))
                _folderStackLoadCancellation = null;
            current.Dispose();
        }
    }

    private void CancelFolderStackLoad()
    {
        var cancellation = _folderStackLoadCancellation;
        _folderStackLoadCancellation = null;
        cancellation?.Cancel();
    }

    private bool PersistFolderStackViewMode(DockFolderStackViewMode mode)
    {
        var settings = Settings;
        var previous = settings.FolderStackViewMode;
        if (previous == mode) return true;

        settings.FolderStackViewMode = mode;
        try
        {
            PersistDockSettings?.Invoke(settings);
            _dockSettings?.Refresh();
            return true;
        }
        catch (StorageException)
        {
            settings.FolderStackViewMode = previous;
            _dockSettings?.Refresh();
            ShowFailureNotice("未能保存堆栈显示方式，已恢复之前的设置。");
            return false;
        }
    }

    private bool PersistFolderStackSortMode(DockFolderStackSortMode mode)
    {
        var settings = Settings;
        var previous = settings.FolderStackSortMode;
        if (previous == mode) return true;

        settings.FolderStackSortMode = mode;
        try
        {
            PersistDockSettings?.Invoke(settings);
            _dockSettings?.Refresh();
            return true;
        }
        catch (StorageException)
        {
            settings.FolderStackSortMode = previous;
            _dockSettings?.Refresh();
            ShowFailureNotice("未能保存堆栈排序方式，已恢复之前的设置。");
            return false;
        }
    }

    private void OpenFolderStackPath(string path, string failureMessage)
    {
        var opened = ShellExecutor.Open(path, null, null);
        if (!opened.Started) ShowFailureNotice(failureMessage);
    }
}
