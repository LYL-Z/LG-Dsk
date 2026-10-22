using System.IO;
using System.Runtime.InteropServices;
using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform.Shell;

public enum FileDropTransferMode
{
    Copy,
    Move,
}

public enum FileDropOperationStatus
{
    Completed,
    PartialOrCancelled,
    InvalidSourceOrDestination,
    Failed,
}

public sealed record FileDropOperationOutcome(
    FileDropOperationStatus Status,
    int ErrorCode,
    int ItemCount);

/// <summary>Uses the Windows Shell to copy or move dropped files into a folder.</summary>
public static class FileDropShell
{
    private const uint FileOperationMove = 1;
    private const uint FileOperationCopy = 2;
    private const int MaximumPathCount = 256;

    public static FileDropOperationOutcome TransferToFolder(
        IEnumerable<string> sources,
        string destination,
        FileDropTransferMode mode,
        nint owner = 0)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (!Enum.IsDefined(mode) || string.IsNullOrWhiteSpace(destination))
            return Invalid(0);

        string[] paths;
        string target;
        try
        {
            if (!Path.IsPathFullyQualified(destination)) return Invalid(0);
            var normalized = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var inputCount = 0;
            foreach (var source in sources)
            {
                if (++inputCount > MaximumPathCount) return Invalid(inputCount);
                if (string.IsNullOrWhiteSpace(source) || !Path.IsPathFullyQualified(source))
                    return Invalid(inputCount);

                var full = Path.GetFullPath(source);
                if (!seen.Add(full)) continue;
                if (IsFileSystemRoot(full) || (!File.Exists(full) && !Directory.Exists(full)))
                    return Invalid(inputCount);
                normalized.Add(full);
            }

            target = Path.GetFullPath(destination);
            if (!Directory.Exists(target) || normalized.Count == 0)
                return Invalid(normalized.Count);

            if (normalized.Any(source => Directory.Exists(source) && IsSameOrDescendant(target, source)))
                return Invalid(normalized.Count);

            paths = normalized.ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
            or NotSupportedException or System.Security.SecurityException)
        {
            return Invalid(0);
        }

        nint from = 0;
        nint to = 0;
        try
        {
            from = Marshal.StringToHGlobalUni(string.Join('\0', paths) + '\0');
            to = Marshal.StringToHGlobalUni(target);

            var operation = new Shell32.ShellFileOperation
            {
                Window = owner,
                Operation = mode == FileDropTransferMode.Copy ? FileOperationCopy : FileOperationMove,
                From = from,
                To = to,
                // Keep the Shell's normal confirmation/progress UI enabled.
                Flags = 0,
            };
            var error = Shell32.SHFileOperation(ref operation);
            if (error != 0)
                return new FileDropOperationOutcome(FileDropOperationStatus.Failed, error, paths.Length);

            return new FileDropOperationOutcome(
                operation.AnyOperationsAborted != 0
                    ? FileDropOperationStatus.PartialOrCancelled
                    : FileDropOperationStatus.Completed,
                0,
                paths.Length);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
            or MarshalDirectiveException)
        {
            return new FileDropOperationOutcome(
                FileDropOperationStatus.Failed,
                Marshal.GetHRForException(ex),
                paths.Length);
        }
        finally
        {
            if (to != 0) Marshal.FreeHGlobal(to);
            if (from != 0) Marshal.FreeHGlobal(from);
        }
    }

    private static FileDropOperationOutcome Invalid(int count) =>
        new(FileDropOperationStatus.InvalidSourceOrDestination, 0, count);

    private static bool IsSameOrDescendant(string destination, string sourceDirectory)
    {
        var source = Path.TrimEndingDirectorySeparator(sourceDirectory);
        var target = Path.TrimEndingDirectorySeparator(destination);
        return string.Equals(target, source, StringComparison.OrdinalIgnoreCase)
            || target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || target.StartsWith(source + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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
