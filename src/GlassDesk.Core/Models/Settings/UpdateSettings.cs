namespace GlassDesk.Core.Models.Settings;

/// <summary>Release discovery preferences. Downloads and installation always require user confirmation.</summary>
public sealed class UpdateSettings
{
    /// <summary>Check for a public stable release at startup and once per 24 hours.</summary>
    public bool CheckAutomatically { get; set; } = true;
}
