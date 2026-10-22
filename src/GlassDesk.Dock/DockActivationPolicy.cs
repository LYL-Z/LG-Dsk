using GlassDesk.Core.Models;

namespace GlassDesk.Dock;

/// <summary>Pure decision rules for whether Dock may launch an application directly.</summary>
public static class DockActivationPolicy
{
    /// <summary>
    /// Requires a deliberate user choice when a valid application entry has no matchable
    /// window and its process state is running-without-window or unknown.
    /// </summary>
    public static bool RequiresExplicitLaunchChoice(AppEntry? entry, AppRuntimeState runtimeState)
        => entry is { IsUsable: true, IsTargetMissing: false }
            && (entry.Kind is AppItemKind.Program or AppItemKind.Shortcut)
            && (runtimeState is AppRuntimeState.RunningWithoutWindow or AppRuntimeState.Unknown);
}
