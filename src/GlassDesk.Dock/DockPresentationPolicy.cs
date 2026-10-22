namespace GlassDesk.Dock;

public enum DockPresentationMode
{
    DesktopDock,
    RunningAppSwitcher,
    RunningAppIsland,
    Hidden,
}

public enum DockForegroundKind { ShellDesktop, ExternalApplication, SecureDesktop, Unknown }
public enum DockTaskbarKind { Visible, AutoHidden, Unknown }
public enum DockFullscreenKind { NotExclusive, Exclusive, Unknown }
public enum DockAwarenessRequest { None, CompactIsland, Switcher }

/// <summary>A single shell snapshot used to choose a safe presentation surface.</summary>
public readonly record struct DockPresentationContext(
    DockForegroundKind Foreground,
    DockTaskbarKind Taskbar,
    DockFullscreenKind Fullscreen,
    DockAwarenessRequest Request,
    bool HasSwitcherContent,
    bool LauncherActive = false,
    bool SettingsWindowVisible = false);

/// <summary>Chooses the smallest Dock surface needed for the current foreground context.</summary>
public static class DockPresentationPolicy
{
    public static DockPresentationMode Resolve(DockPresentationContext context)
    {
        if (context.SettingsWindowVisible
            || context.Foreground is DockForegroundKind.SecureDesktop or DockForegroundKind.Unknown
            || context.Fullscreen == DockFullscreenKind.Unknown)
            return DockPresentationMode.Hidden;

        // A user-requested switcher is an explicit action, so it may be shown over
        // a known exclusive fullscreen app. Passive surfaces remain suppressed.
        if (context.Request == DockAwarenessRequest.Switcher
            && context.Foreground == DockForegroundKind.ExternalApplication
            && context.HasSwitcherContent)
            return DockPresentationMode.RunningAppSwitcher;

        if (context.Fullscreen == DockFullscreenKind.Exclusive)
            return DockPresentationMode.Hidden;

        if (context.LauncherActive || context.Foreground == DockForegroundKind.ShellDesktop)
            return DockPresentationMode.DesktopDock;

        if (context.Foreground != DockForegroundKind.ExternalApplication)
            return DockPresentationMode.Hidden;

        if (context.Request == DockAwarenessRequest.CompactIsland
            && context.Taskbar == DockTaskbarKind.AutoHidden)
            return DockPresentationMode.RunningAppIsland;

        // The Dock is a normal desktop surface while an ordinary application is
        // foreground. Auto-hide is a separate user preference; foreground alone
        // must not make the launcher's entry points disappear.
        return DockPresentationMode.DesktopDock;
    }

    public static bool EffectiveExternalForeground(bool externalApplicationForeground, bool settingsWindowVisible) =>
        externalApplicationForeground || settingsWindowVisible;
}
