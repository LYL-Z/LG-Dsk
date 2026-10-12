using System.Collections.Concurrent;
using System.Diagnostics;
using GlassDesk.Platform.Displays;

namespace GlassDesk.Dock.Tests;

public sealed class IdleCaptureBufferTests
{
    [Fact]
    public void ShortHidesStayWarmAndLongHidesReleaseBuffersWithoutRestartingNativeCapture()
    {
        var traces = new ConcurrentQueue<CaptureTrace>();
        var carriers = new ConcurrentQueue<nint>();
        using var feed = new SharedBackdropFeed { TraceObserved = traces.Enqueue, CaptureStarting = carriers.Enqueue };
        var region = new PixelRect(50, 50, 320, 240);
        _ = Read(feed, region);
        feed.Suspend();
        Thread.Sleep(200);
        Assert.DoesNotContain(traces, trace => trace.Stage == "CaptureBuffers" && trace.Reason == "Released");
        _ = Read(feed, region);
        feed.Suspend();
        var wait = Stopwatch.StartNew();
        while (wait.Elapsed < TimeSpan.FromSeconds(7)
            && !traces.Any(trace => trace.Stage == "CaptureBuffers" && trace.Reason == "Released"))
        {
            // Repeated hidden UI polls must not restart the retention deadline.
            feed.Suspend();
            Thread.Sleep(20);
        }
        Assert.Single(traces, trace => trace.Stage == "CaptureBuffers" && trace.Reason == "Released");
        var fresh = Read(feed, region);
        Assert.Equal(160 * 120 * 4, fresh.Pixels.Length);
        Assert.Single(carriers.Distinct());
        Assert.Single(traces, trace => trace.Stage == "GraphicsDevice" && trace.Reason == "Shared");
        Assert.Equal(2, traces.Count(trace => trace.Stage == "ReadbackResource" && trace.Reason == "Created"));
    }

    private static BackdropFrame Read(SharedBackdropFeed feed, PixelRect region)
    {
        var wait = Stopwatch.StartNew();
        while (wait.Elapsed < TimeSpan.FromSeconds(4))
        {
            if (feed.TryRead(region, 160, 120, out var frame) && frame is not null) return frame;
            Thread.Sleep(10);
        }
        throw new TimeoutException("Compatible native capture did not resume: " + feed.Failure);
    }
}
