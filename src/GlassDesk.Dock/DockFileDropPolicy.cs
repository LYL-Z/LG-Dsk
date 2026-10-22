using System.IO;
using System.Windows;

namespace GlassDesk.Dock;

/// <summary>Chooses an OLE effect without changing files; the Shell performs the operation on Drop.</summary>
internal static class DockFileDropPolicy
{
    private const int MaximumPathCount = 256;

    public static DragDropEffects Resolve(
        DockTileModel tile,
        IReadOnlyList<string> paths,
        DragDropKeyStates keyStates,
        DragDropEffects allowedEffects)
    {
        ArgumentNullException.ThrowIfNull(tile);
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count is 0 or > MaximumPathCount
            || !PathsAreFullyQualified(paths))
        {
            return DragDropEffects.None;
        }

        if (tile.AcceptsFileDrop)
            return Allows(allowedEffects, DragDropEffects.Copy) ? DragDropEffects.Copy : DragDropEffects.None;

        return tile.FolderDropPath is { Length: > 0 } destination
            ? ResolveFolder(paths, destination, keyStates, allowedEffects)
            : DragDropEffects.None;
    }

    private static DragDropEffects ResolveFolder(
        IReadOnlyList<string> paths,
        string destination,
        DragDropKeyStates keyStates,
        DragDropEffects allowedEffects)
    {
        if (paths.Count is 0 or > MaximumPathCount || (keyStates & DragDropKeyStates.AltKey) != 0)
            return DragDropEffects.None;

        var control = (keyStates & DragDropKeyStates.ControlKey) != 0;
        var shift = (keyStates & DragDropKeyStates.ShiftKey) != 0;
        if (control && shift) return DragDropEffects.None;
        if (control) return Allows(allowedEffects, DragDropEffects.Copy) ? DragDropEffects.Copy : DragDropEffects.None;
        if (shift) return Allows(allowedEffects, DragDropEffects.Move) ? DragDropEffects.Move : DragDropEffects.None;

        try
        {
            if (!Path.IsPathFullyQualified(destination)) return DragDropEffects.None;
            var destinationRoot = Path.GetPathRoot(Path.GetFullPath(destination));
            if (string.IsNullOrWhiteSpace(destinationRoot)) return DragDropEffects.None;

            var sameVolume = true;
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return DragDropEffects.None;
                var sourceRoot = Path.GetPathRoot(Path.GetFullPath(path));
                if (!string.Equals(sourceRoot, destinationRoot, StringComparison.OrdinalIgnoreCase))
                {
                    sameVolume = false;
                }
            }

            var preferred = sameVolume ? DragDropEffects.Move : DragDropEffects.Copy;
            if (Allows(allowedEffects, preferred)) return preferred;
            var fallback = preferred == DragDropEffects.Move ? DragDropEffects.Copy : DragDropEffects.Move;
            return Allows(allowedEffects, fallback) ? fallback : DragDropEffects.None;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException
            or System.Security.SecurityException)
        {
            return DragDropEffects.None;
        }
    }

    private static bool Allows(DragDropEffects allowed, DragDropEffects effect) => (allowed & effect) == effect;

    private static bool PathsAreFullyQualified(IReadOnlyList<string> paths)
    {
        try
        {
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException
            or System.Security.SecurityException)
        {
            return false;
        }
    }
}
