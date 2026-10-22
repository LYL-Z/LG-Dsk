using System.IO;
using System.Runtime.InteropServices;
using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform.Shell;

/// <summary>回收站查询得到的系统状态；null 代表 Windows Shell 没有给出可信答案。</summary>
public sealed record RecycleBinContents(long ItemCount, long SizeBytes);

public enum RecycleBinOperationStatus
{
    Completed,
    PartialOrCancelled,
    InvalidSource,
    Failed,
}

/// <summary>把文件交给 Windows Shell 后的可观察结果。</summary>
public sealed record RecycleBinOperationOutcome(
    RecycleBinOperationStatus Status,
    int ErrorCode,
    int RequestedItemCount);

/// <summary>
/// 通过 Windows Shell 查询、清空和回收文件。这里不调用 File.Delete，
/// 也不在 Shell 拒绝后改走永久删除。
/// </summary>
public static class RecycleBinShell
{
    private const uint FileOperationDelete = 3;

    /// <summary>查询所有驱动器的回收站；Shell API 失败时返回 null。</summary>
    public static RecycleBinContents? Query()
    {
        try
        {
            var info = new Shell32.ShellQueryRecycleBinInfo
            {
                Size = (uint)Marshal.SizeOf<Shell32.ShellQueryRecycleBinInfo>(),
            };
            var result = Shell32.SHQueryRecycleBin(null, ref info);
            return result < 0 || info.Items < 0 || info.Bytes < 0
                ? null
                : new RecycleBinContents(info.Items, info.Bytes);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
            or MarshalDirectiveException)
        {
            return null;
        }
    }

    /// <summary>
    /// 使用 FO_DELETE + FOF_ALLOWUNDO 回收完整路径。文件系统根目录、缺失路径和无效输入会在调用 Shell 前拒绝。
    /// </summary>
    public static RecycleBinOperationOutcome MoveToRecycleBin(IEnumerable<string> sources, nint owner = 0)
    {
        ArgumentNullException.ThrowIfNull(sources);

        string[] paths;
        try
        {
            paths = sources
                .Select(path => string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path))
                .Where(path => path is not null)
                .Select(path => path!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
            or NotSupportedException)
        {
            return new RecycleBinOperationOutcome(RecycleBinOperationStatus.InvalidSource, 0, 0);
        }

        if (paths.Length == 0 || paths.Any(path => IsFileSystemRoot(path)
            || (!File.Exists(path) && !Directory.Exists(path))))
        {
            return new RecycleBinOperationOutcome(RecycleBinOperationStatus.InvalidSource, 0, paths.Length);
        }

        // SHFileOperation expects a double-null-terminated list. StringToHGlobalUni adds
        // the final terminator, so the managed string supplies the preceding one.
        var multiString = string.Join('\0', paths) + '\0';
        var from = Marshal.StringToHGlobalUni(multiString);
        try
        {
            var operation = new Shell32.ShellFileOperation
            {
                Window = owner,
                Operation = FileOperationDelete,
                From = from,
                Flags = Shell32.FileOperationAllowUndo,
            };
            var error = Shell32.SHFileOperation(ref operation);

            if (error != 0)
                return new RecycleBinOperationOutcome(RecycleBinOperationStatus.Failed, error, paths.Length);

            return new RecycleBinOperationOutcome(
                operation.AnyOperationsAborted != 0
                    ? RecycleBinOperationStatus.PartialOrCancelled
                    : RecycleBinOperationStatus.Completed,
                0,
                paths.Length);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
            or MarshalDirectiveException)
        {
            return new RecycleBinOperationOutcome(
                RecycleBinOperationStatus.Failed,
                Marshal.GetHRForException(ex),
                paths.Length);
        }
        finally
        {
            Marshal.FreeHGlobal(from);
        }
    }

    /// <summary>清空所有驱动器；调用者负责先做独立确认。</summary>
    public static int EmptyAllAfterConfirmation(nint owner)
    {
        try
        {
            return Shell32.SHEmptyRecycleBin(owner, null, Shell32.ShEmptyRecycleBinNoConfirmation);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
            or MarshalDirectiveException)
        {
            return Marshal.GetHRForException(ex);
        }
    }

    private static bool IsFileSystemRoot(string path)
    {
        var root = Path.GetPathRoot(path);
        return root is not null
            && string.Equals(
                Path.TrimEndingDirectorySeparator(path),
                Path.TrimEndingDirectorySeparator(root),
                StringComparison.OrdinalIgnoreCase);
    }
}
