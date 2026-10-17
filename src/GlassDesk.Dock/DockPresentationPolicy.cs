namespace GlassDesk.Dock;

public enum DockPresentationMode
{
    DesktopDock,
    RunningAppSwitcher,
    RunningAppIsland,
    Hidden,
}

/// <summary>Chooses the smallest Dock surface needed for the current foreground context.</summary>
public static class DockPresentationPolicy
{
    public static bool EffectiveExternalForeground(bool externalApplicationForeground, bool settingsWindowVisible) =>
        externalApplicationForeground || settingsWindowVisible;

    public static DockPresentationMode Resolve(
        bool launcherActive,
        bool externalApplicationForeground,
        bool foregroundIsFullscreen,
        bool hasSwitchableApplications,
        bool switcherRequested,
        bool settingsWindowVisible = false,
        bool appIslandRequested = false)
    {
        if (settingsWindowVisible)
            return DockPresentationMode.Hidden;

        if (launcherActive || !externalApplicationForeground)
            return DockPresentationMode.DesktopDock;

        if (switcherRequested)
            return DockPresentationMode.RunningAppSwitcher;

        // The host admits this surface only after its event-based fullscreen
        // observation and Windows taskbar auto-hide check agree. Trust that
        // context here so exclusive-D3D fullscreen sessions are not rejected
        // by the cheaper geometry-only foreground probe.
        if (appIslandRequested)
            return DockPresentationMode.RunningAppIsland;

        return DockPresentationMode.Hidden;
    }
}
