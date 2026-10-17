namespace GlassDesk.Core.Models.Settings;

/// <summary>Performance policy; session-only manual sleep is deliberately not persisted.</summary>
public sealed class PerformanceSettings
{
    public bool AutoSuspendWhenFullscreen { get; set; } = true;

    /// <summary>Show a compact application island when the taskbar auto-hides during a fullscreen session.</summary>
    public bool ShowFullscreenAppIsland { get; set; } = true;
}
