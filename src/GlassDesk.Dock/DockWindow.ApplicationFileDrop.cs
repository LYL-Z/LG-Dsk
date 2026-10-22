using System.Windows;
using GlassDesk.Core.Models;
using GlassDesk.Platform.Shell;

namespace GlassDesk.Dock;

public partial class DockWindow
{
    private void OnTileFileDropRequested(object? sender, DockTileFileDropEventArgs e) =>
        e.CompletedEffect = ExecuteFileDrop(e.EntryId, e.Paths, e.RequestedEffect);

    private void OnApplicationFileDragOver(object sender, DragEventArgs e)
    {
        var status = DockFileDropData.TryRead(e.Data, out var paths);
        if (status == DockFileDropDataStatus.NotPresent) return;

        var model = (sender as FrameworkElement)?.DataContext as DockTileModel;
        e.Effects = status != DockFileDropDataStatus.Valid || model is null
            ? DragDropEffects.None
            : DockFileDropPolicy.Resolve(model, paths, e.KeyStates, e.AllowedEffects);
        e.Handled = true;
    }

    private void OnRecentApplicationFileDrop(object sender, DragEventArgs e)
    {
        var status = DockFileDropData.TryRead(e.Data, out var paths);
        if (status == DockFileDropDataStatus.NotPresent) return;
        e.Handled = true;
        e.Effects = DragDropEffects.None;

        if (sender is not FrameworkElement { DataContext: DockTileModel model }) return;
        if (status != DockFileDropDataStatus.Valid)
        {
            ShowFailureNotice("Windows 未能读取拖入的文件列表。");
            return;
        }

        var requestedEffect = DockFileDropPolicy.Resolve(model, paths, e.KeyStates, e.AllowedEffects);
        if (requestedEffect != DragDropEffects.None)
            e.Effects = ExecuteFileDrop(model.EntryId, paths, requestedEffect);
    }

    private DragDropEffects ExecuteFileDrop(
        string entryId,
        IReadOnlyList<string> paths,
        DragDropEffects requestedEffect)
    {
        var services = _services;
        if (services is null) return DragDropEffects.None;

        _autoHide?.PushSuppress(DateTime.UtcNow);
        try
        {
            var entry = services.Library.Find(entryId);
            if (entry is null)
            {
                ShowFailureNotice("该 Dock 项已不存在，文件没有被处理。");
                return DragDropEffects.None;
            }

            if (entry.Kind == AppItemKind.Folder)
            {
                if (requestedEffect is not (DragDropEffects.Copy or DragDropEffects.Move))
                    return DragDropEffects.None;

                var mode = requestedEffect == DragDropEffects.Copy
                    ? FileDropTransferMode.Copy
                    : FileDropTransferMode.Move;
                var transfer = FileDropShell.TransferToFolder(paths, entry.Target, mode, Handle);
                switch (transfer.Status)
                {
                    case FileDropOperationStatus.Completed:
                        ShowTemporaryNotice(mode == FileDropTransferMode.Copy
                            ? $"已将 {transfer.ItemCount} 项复制到“{entry.DisplayName}”。"
                            : $"已将 {transfer.ItemCount} 项移动到“{entry.DisplayName}”。");
                        return requestedEffect;
                    case FileDropOperationStatus.PartialOrCancelled:
                        ShowFailureNotice("文件操作被取消或只完成了一部分；请核对来源和目标文件夹。");
                        return DragDropEffects.None;
                    case FileDropOperationStatus.InvalidSourceOrDestination:
                        ShowFailureNotice("来源项目或目标文件夹无效/不可访问；未报告操作成功。");
                        return DragDropEffects.None;
                    default:
                        ShowFailureNotice($"Windows 未能完成文件操作（0x{unchecked((uint)transfer.ErrorCode):X8}）。");
                        return DragDropEffects.None;
                }
            }

            if (requestedEffect != DragDropEffects.Copy)
                return DragDropEffects.None;

            var result = services.Launch.LaunchWithFiles(entryId, paths);
            if (!result.Succeeded)
            {
                ShowFailureNotice(result.Message ?? "未能将文件交给该应用。");
                return DragDropEffects.None;
            }

            var name = entry.DisplayName;
            ShowTemporaryNotice(string.IsNullOrWhiteSpace(name)
                ? "文件已交给应用。"
                : $"文件已交给“{name}”。");
            return DragDropEffects.Copy;
        }
        finally
        {
            _autoHide?.PopSuppress(DateTime.UtcNow);
        }
    }
}
