using System.Collections.Concurrent;
using System.Diagnostics;
using GlassDesk.Platform.Displays;

namespace GlassDesk.Dock.Tests;

public sealed class IdleCaptureBufferTests
{
    [Fact]
    public void OrdinaryHideStaysWarmPastFiveSecondsWithoutCaptureWorkButGameSleepRetiresImmediately()
    {
        IdleCapture? capture = null;
        using var feed = new SharedBackdropFeed((bounds, _, _) => capture = new IdleCapture(bounds));
        using (Read(feed, new PixelRect(50, 50, 320, 240))) { }
        feed.Suspend();
        Thread.Sleep(100);
        var reads = capture!.Reads;
        Thread.Sleep(6000);
        Assert.False(capture.Disposed);
        Assert.Equal(reads, capture.Reads);
        feed.SetPerformanceSleeping(true);
        Assert.True(SpinWait.SpinUntil(() => capture.Disposed, 2000));
    }

    [Fact]
    public void ShortHidesStayWarmAndLongHidesRetireTheHelperBeforeResume()
    {
        var traces = new ConcurrentQueue<CaptureTrace>();
        var carriers = new ConcurrentQueue<nint>();
        using var feed = new SharedBackdropFeed((bounds,_,trace)=>new RecoveringDesktopSceneCapture(bounds,trace,
            CompatibilityProbePath.File("GlassCaptureProbe.exe")), TimeSpan.FromSeconds(1)) { TraceObserved = traces.Enqueue, CaptureStarting = carriers.Enqueue };
        var region = new PixelRect(50, 50, 320, 240);
        var baselineFamily=CaptureProcessFamily.Snapshot();
        using(var initial=Read(feed, region, traces)) { }
        var firstFamily=CaptureProcessFamily.KnownProcesses.Except(baselineFamily).ToArray();
        feed.Suspend();
        Thread.Sleep(200);
        Assert.DoesNotContain(traces, trace => trace.Stage == "CaptureBuffers" && trace.Reason == "Released");
        using(var warm=Read(feed, region)) { }
        Assert.Equal(firstFamily,CaptureProcessFamily.KnownProcesses.Except(baselineFamily).ToArray());
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
        Assert.All(firstFamily,pid=>Assert.DoesNotContain(pid,CaptureProcessFamily.KnownProcesses));
        using var fresh = Read(feed, region, traces);
        Assert.Equal(160 * 120 * 4, fresh.Pixels.Length);
        Assert.DoesNotContain(CaptureProcessFamily.KnownProcesses.Except([Environment.ProcessId]),pid=>firstFamily.Contains(pid));
        Assert.Single(traces,trace=>trace.Stage=="CaptureOwner" && trace.Reason=="Retired");
    }

    [Fact]
    public void LongHideRetiresAndRecreatesTheCaptureWorkerWithoutNativeDesktopCapture()
    {
        var traces = new ConcurrentQueue<CaptureTrace>();
        var captures = new ConcurrentQueue<IdleCapture>();
        using var feed = new SharedBackdropFeed((bounds, _, _) =>
        {
            var capture = new IdleCapture(bounds);
            captures.Enqueue(capture);
            return capture;
        }, TimeSpan.FromMilliseconds(120)) { TraceObserved = traces.Enqueue };
        var region = new PixelRect(50, 50, 320, 240);

        using (Read(feed, region)) { }
        feed.Suspend();

        Assert.True(SpinWait.SpinUntil(
            () => traces.Any(trace => trace.Stage == "CaptureOwner" && trace.Reason == "Retired"),
            TimeSpan.FromSeconds(2)));
        Assert.Single(captures);
        Assert.True(captures.Single().Disposed);

        using var fresh = Read(feed, region);

        Assert.Equal(2, captures.Count);
        Assert.Equal(160 * 120 * 4, fresh.Pixels.Length);
    }

    private static BackdropFrame Read(SharedBackdropFeed feed, PixelRect region,
        ConcurrentQueue<CaptureTrace>? traces = null)
    {
        var wait = Stopwatch.StartNew();
        while (wait.Elapsed < TimeSpan.FromSeconds(4))
        {
            if (feed.TryRead(region, 160, 120, out var frame) && frame is not null) return frame;
            Thread.Sleep(10);
        }
        var timeline = traces is null
            ? "(no native trace requested)"
            : string.Join(" | ", traces
                .Where(trace => trace.Stage is "Consumer" or "CaptureOwner" or "CaptureBuffers"
                    or "Recovery" or "BackdropProvider" or "CaptureFault" or "SourceClock")
                .TakeLast(48)
                .Select(trace =>
                    $"{Stopwatch.GetElapsedTime(trace.AtTicks).TotalMilliseconds:0}ms ago {trace.Stage}:{trace.Reason}"));
        var performance = System.Text.Json.JsonSerializer.Serialize(feed.InspectPerformance());
        throw new TimeoutException($"Compatible native capture did not resume. failure={feed.Failure ?? "(none)"}; " +
            $"performance={performance}; trace={timeline}");
    }

    private sealed class IdleCapture(ScreenRect bounds) : IDesktopSceneCapture
    {
        internal volatile bool Disposed;
        internal int Reads;
        public nint CarrierHandle => 0;
        public bool UpdateExclusions() => false;
        public CapturePixels? TryRead(TimeSpan waitForFrame = default, CancellationToken cancellation = default,
            long sourceNotBeforeTicks = 0, bool reusePixels = false)
        {
            Interlocked.Increment(ref Reads);
            return new(bounds.Width, bounds.Height, new byte[bounds.Width * bounds.Height * 4])
                { SourceQpcTicks = Stopwatch.GetTimestamp() };
        }
        public void ReleaseReadbackBuffers() { }
        public void Dispose() => Disposed = true;
    }
}
