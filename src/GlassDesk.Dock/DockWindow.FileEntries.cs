using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Automation;
using System.Windows.Threading;
using GlassDesk.Platform.Shell;
using GlassDesk.Platform;

namespace GlassDesk.Dock;

public partial class DockWindow
{
    private DispatcherTimer? _recycleBinStatusTimer;
    private RecycleBinContents? _recycleBinContents;
    private int _recycleBinRefreshInProgress;
    private int _recycleBinRefreshPending;
    private bool _recycleBinStatusClosed;

    private void InitializeFileEntries()
    {
        RefreshRecycleBinStatus();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(20),
        };
        _recycleBinStatusTimer = timer;
        timer.Tick += (_, _) => RefreshRecycleBinStatus();
        timer.Start();
        Closed += (_, _) =>
        {
            _recycleBinStatusClosed = true;
            timer.Stop();
        };
    }

    private async void RefreshRecycleBinStatus()
    {
        if (_recycleBinStatusClosed) return;
        if (Interlocked.CompareExchange(ref _recycleBinRefreshInProgress, 1, 0) != 0)
        {
            Interlocked.Exchange(ref _recycleBinRefreshPending, 1);
            return;
        }

        try
        {
            do
            {
                Interlocked.Exchange(ref _recycleBinRefreshPending, 0);
                RecycleBinContents? contents;
                try
                {
                    contents = await RunShellOperationOnSta(RecycleBinShell.Query);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    contents = null;
                }

                if (_recycleBinStatusClosed) return;

                _recycleBinContents = contents;
                RecycleBinContentsOverlay.Visibility = contents is { ItemCount: > 0 }
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                var status = contents switch
                {
                    { ItemCount: 0 } => "回收站（空）",
                    { ItemCount: > 0 } filled => $"回收站（{filled.ItemCount} 项）",
                    _ => "回收站（状态未知）",
                };
                RecycleBinButton.ToolTip = status;
                AutomationProperties.SetName(RecycleBinButton, $"打开{status}");
            }
            while (Interlocked.Exchange(ref _recycleBinRefreshPending, 0) != 0);
        }
        finally
        {
            Interlocked.Exchange(ref _recycleBinRefreshInProgress, 0);
            if (!_recycleBinStatusClosed && Interlocked.Exchange(ref _recycleBinRefreshPending, 0) != 0)
                _ = Dispatcher.BeginInvoke(new Action(RefreshRecycleBinStatus));
        }
    }

    private void OnRecycleBinDragOver(object sender, DragEventArgs e)
    {
        var status = DockFileDropData.TryRead(e.Data, out _);
        var acceptsFiles = status == DockFileDropDataStatus.Valid
            && (e.AllowedEffects & DragDropEffects.Move) != 0;
        e.Effects = acceptsFiles ? DragDropEffects.Move : DragDropEffects.None;
        RecycleBinButton.Opacity = acceptsFiles ? .78 : 1;
        e.Handled = true;
    }

    private void OnRecycleBinDragLeave(object sender, DragEventArgs e)
    {
        RecycleBinButton.Opacity = 1;
        e.Handled = true;
    }

    private void OnRecycleBinDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        RecycleBinButton.Opacity = 1;
        var status = DockFileDropData.TryRead(e.Data, out var paths);
        if (status != DockFileDropDataStatus.Valid || (e.AllowedEffects & DragDropEffects.Move) == 0)
        {
            e.Effects = DragDropEffects.None;
            if (status == DockFileDropDataStatus.Invalid)
                ShowFailureNotice("Windows 未能读取拖入的文件列表。");
            return;
        }

        _autoHide?.PushSuppress(DateTime.UtcNow);
        try
        {
            var result = RecycleBinShell.MoveToRecycleBin(paths, Handle);
            e.Effects = result.Status == RecycleBinOperationStatus.Completed
                ? DragDropEffects.Move
                : DragDropEffects.None;

            switch (result.Status)
            {
                case RecycleBinOperationStatus.Completed:
                    ShowTemporaryNotice(paths.Length == 1 ? "已放入回收站。" : $"已将 {paths.Length} 项放入回收站。");
                    break;
                case RecycleBinOperationStatus.PartialOrCancelled:
                    ShowFailureNotice("部分文件未能放入回收站，或操作已取消；请核对源文件和回收站内容。");
                    break;
                case RecycleBinOperationStatus.InvalidSource:
                    ShowFailureNotice("至少有一个拖入路径无效或不能回收；源文件未由 LG Dsk 永久删除。");
                    break;
                default:
                    ShowFailureNotice($"Windows 未能将文件放入回收站（0x{unchecked((uint)result.ErrorCode):X8}）。");
                    break;
            }
        }
        catch (Exception)
        {
            e.Effects = DragDropEffects.None;
            ShowFailureNotice("Windows 文件操作未能完成；请检查源文件和回收站内容。");
        }
        finally
        {
            _autoHide?.PopSuppress(DateTime.UtcNow);
            RefreshRecycleBinStatus();
        }
    }

    private void OnRecycleBinRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        nint menuHandle = 0;
        var foregroundBefore = WindowControl.GetForegroundWindow();
        menu.Opened += (_, _) =>
            menuHandle = (PresentationSource.FromVisual(menu) as System.Windows.Interop.HwndSource)?.Handle ?? 0;
        _ = menu.Items.Add(MenuItem("打开回收站", () => OnRecycleBinButtonClick(this, new RoutedEventArgs())));
        _ = menu.Items.Add(new Separator());
        var empty = MenuItem("清空回收站…", () =>
            _ = Dispatcher.BeginInvoke(new Action(EmptyRecycleBinAfterConfirmation)));
        empty.IsEnabled = _recycleBinContents?.ItemCount != 0;
        _ = menu.Items.Add(empty);

        _autoHide?.PushSuppress(DateTime.UtcNow);
        menu.Closed += (_, _) =>
        {
            _autoHide?.PopSuppress(DateTime.UtcNow);
            RestorePopupForeground(foregroundBefore, menuHandle);
        };
        _ = WindowActivation.TryBringToForeground(Handle);
        menu.IsOpen = true;
    }

    private async void EmptyRecycleBinAfterConfirmation()
    {
        if (_recycleBinContents is { ItemCount: 0 })
        {
            ShowTemporaryNotice("回收站已经为空。");
            return;
        }

        var answer = MessageBox.Show(
            this,
            "清空回收站会永久删除其中的所有项目，之后无法从回收站恢复。确定继续吗？",
            "清空回收站",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        _autoHide?.PushSuppress(DateTime.UtcNow);
        try
        {
            var owner = Handle;
            var result = await RunShellOperationOnSta(() => RecycleBinShell.EmptyAllAfterConfirmation(owner));
            if (result >= 0)
                ShowTemporaryNotice("回收站已清空。");
            else
                ShowFailureNotice($"Windows 未能清空回收站（0x{unchecked((uint)result):X8}）。");
        }
        catch (Exception)
        {
            ShowFailureNotice("Windows 清空回收站操作失败；未报告成功。");
        }
        finally
        {
            _autoHide?.PopSuppress(DateTime.UtcNow);
            RefreshRecycleBinStatus();
        }
    }

    private static Task<T> RunShellOperationOnSta<T>(Func<T> operation)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.TrySetResult(operation()); }
            catch (Exception exception) { completion.TrySetException(exception); }
        })
        {
            IsBackground = true,
            Name = "LG Dsk Windows Shell operation",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
