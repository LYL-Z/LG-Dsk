internal interface IDesktopSceneCapture : IDisposable
{
    nint CarrierHandle { get; }
    bool AllowsStaticContent => false;
    long SceneRevision => 0;
    bool UpdateExclusions();
    CapturePixels? TryRead(TimeSpan waitForFrame = default, CancellationToken cancellation = default,
        long sourceNotBeforeTicks = 0, bool reusePixels = false);
    void ReleaseReadbackBuffers();
}
