using System.Runtime.InteropServices;
using System.Windows;

namespace GlassDesk.Dock;

internal enum DockFileDropDataStatus
{
    NotPresent,
    Invalid,
    Valid,
}

/// <summary>Reads OLE file-drop data defensively; hostile or unavailable formats fail closed.</summary>
internal static class DockFileDropData
{
    internal static DockFileDropDataStatus TryRead(IDataObject data, out string[] paths)
    {
        ArgumentNullException.ThrowIfNull(data);
        paths = [];

        try
        {
            if (!data.GetDataPresent(DataFormats.FileDrop, true)) return DockFileDropDataStatus.NotPresent;
            if (data.GetData(DataFormats.FileDrop, true) is not string[] { Length: > 0 } result)
                return DockFileDropDataStatus.Invalid;

            paths = result;
            return DockFileDropDataStatus.Valid;
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or ArgumentException)
        {
            return DockFileDropDataStatus.Invalid;
        }
    }

    internal static bool TryReadString(IDataObject data, string format, out string value)
    {
        ArgumentNullException.ThrowIfNull(data);
        value = string.Empty;

        try
        {
            if (!data.GetDataPresent(format)) return false;
            if (data.GetData(format) is not string result || string.IsNullOrWhiteSpace(result)) return false;
            value = result;
            return true;
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }
}
