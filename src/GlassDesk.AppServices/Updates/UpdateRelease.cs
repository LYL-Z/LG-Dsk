namespace GlassDesk.AppServices.Updates;

public sealed record UpdateRelease(
    Version Version, string FileName, Uri DownloadUrl, string Sha256, long Size, string Notes, Uri ReleasePage)
{
    /// <summary>Present only after validating a signed publisher manifest.</summary>
    public IReadOnlyList<UpdateChunk> Chunks { get; init; } = Array.Empty<UpdateChunk>();
    public Uri? ChunkSource { get; init; }
}

public sealed record UpdateChunk(string Sha256, long Size);
