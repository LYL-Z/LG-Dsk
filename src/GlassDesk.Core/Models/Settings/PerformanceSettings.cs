namespace GlassDesk.Core.Models.Settings;

/// <summary>Performance policy; session-only manual sleep is deliberately not persisted.</summary>
public sealed class PerformanceSettings
{
    public bool AutoSuspendWhenFullscreen { get; set; } = true;

    /// <summary>Show a compact application island for external foreground apps while the Windows taskbar auto-hides.</summary>
    public bool ShowFullscreenAppIsland { get; set; } = true;
}
