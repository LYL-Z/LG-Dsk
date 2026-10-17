namespace GlassDesk.Dock;

public enum DockPresentationMode
{
    DesktopDock,
    RunningAppSwitcher,
    Hidden,
}

/// <summary>Chooses the smallest Dock surface needed for the current foreground context.</summary>
public static class DockPresentationPolicy
{
    public static DockPresentationMode Resolve(
        bool launcherActive,
        bool externalApplicationForeground,
        bool foregroundIsFullscreen,
        bool hasSwitchableApplications,
        bool switcherRequested)
    {
        if (launcherActive || !externalApplicationForeground)
            return DockPresentationMode.DesktopDock;

        if (switcherRequested && !foregroundIsFullscreen && hasSwitchableApplications)
            return DockPresentationMode.RunningAppSwitcher;

        return DockPresentationMode.Hidden;
    }
}
