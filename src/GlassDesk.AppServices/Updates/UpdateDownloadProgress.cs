namespace GlassDesk.AppServices.Updates;

/// <summary>Download progress with retry and resume context for an update UI.</summary>
public sealed record UpdateDownloadProgress(
    int Percent,
    long ReceivedBytes,
    long TotalBytes,
    int Attempt,
    bool IsResuming);
