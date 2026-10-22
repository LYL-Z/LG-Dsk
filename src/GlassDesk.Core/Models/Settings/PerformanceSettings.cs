namespace GlassDesk.Core.Models.Settings;

/// <summary>Performance policy; session-only manual sleep is deliberately not persisted.</summary>
public sealed class PerformanceSettings
{
    public bool AutoSuspendWhenFullscreen { get; set; } = true;

    /// <summary>Show a compact application island for external foreground apps while the Windows taskbar auto-hides.</summary>
    public bool ShowFullscreenAppIsland { get; set; } = true;

    /// <summary>Expand the compact island when the pointer enters it; clicking remains available when disabled.</summary>
    public bool ExpandIslandOnHover { get; set; } = true;

    /// <summary>Include notification-area items exposed through Windows accessibility APIs in the switcher.</summary>
    public bool ShowTrayIcons { get; set; } = true;
}
