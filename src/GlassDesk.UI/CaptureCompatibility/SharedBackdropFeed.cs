using System.Diagnostics;
using System.Runtime.InteropServices;
using GlassDesk.Platform.Displays;
using GlassDesk.UI;

// Independent sample; owns all native resources on a single background STA.
internal sealed class SharedBackdropFeed : IDisposable
{
    private const int CaptureCadenceMilliseconds=16;
    private static readonly TimeSpan IdleBufferRetention=TimeSpan.FromSeconds(5);
    private readonly object _gate=new();
    private LatestBackdropState? _state;
    private Thread? _thread;
    private CancellationTokenSource? _stop;
    private bool _stopping;
    private string? _failure;
    private readonly BackdropProviderPolicy _policy = new();
    private long _signalGeneration = BackdropRecovery.Generation;
    internal string? Failure => Volatile.Read(ref _failure);
    internal object InspectTiming()
    {
        lock(_gate) return new {failure=Failure,stopping=_stopping,workerAlive=_thread?.IsAlive,
            qpcFrequency=Stopwatch.Frequency,state=_state?.InspectTiming(Stopwatch.GetTimestamp())};
    }
    private readonly Action<LatestBackdropState,CancellationToken> _run;
    private readonly Func<ScreenRect,bool,Action<CaptureTrace>?,IDesktopSceneCapture> _create = DesktopSceneCaptureFactory.Create;
    internal Action<CapturePixels,byte[]>? CaptureObserved { get; init; }
    internal Action<nint>? CaptureStarting { get; init; }
    internal bool DiagnosticCarrierOnScreen { get; init; }
    internal Action<CaptureTrace>? TraceObserved {get;set;} = BackdropRecovery.Record;
    internal SharedBackdropFeed() => _run=Run;
    // The capture loop is the external/native boundary; lifecycle ownership stays here.
    internal SharedBackdropFeed(Action<LatestBackdropState,CancellationToken> run) => _run=run;
    internal SharedBackdropFeed(Func<ScreenRect,bool,Action<CaptureTrace>?,IDesktopSceneCapture> create)
    { _run=Run; _create=create ?? throw new ArgumentNullException(nameof(create)); }
    internal bool TryRead(PixelRect region,int width,int height,out BackdropFrame? frame)
    {
        frame=null;
        lock(_gate)
        {
            if (_signalGeneration != BackdropRecovery.Generation)
            {
                _signalGeneration = BackdropRecovery.Generation;
                _state?.InvalidateScene(); _policy.RecordSuccess();
            }
            if (!_stopping && _state is not null && _thread?.IsAlive == false)
            {
                // A disposed state from a transient native failure is not terminal.
                // The old worker must finish before retrying; rate-limit failures.
                if (!_policy.RetryAllowed(Stopwatch.GetTimestamp())) return false;
                _thread = null; _state = null; _stop = null;
            }
            if(_stopping)
            {
                // Retain ownership while native capture unwinds. Never overlap workers.
                if(_thread?.IsAlive==true)
                {TraceObserved?.Invoke(new("Consumer","WorkerStopping",Stopwatch.GetTimestamp()));return false;}
                _thread=null;_state=null;_stop=null;_stopping=false;
            }
            if(_state is null)
            {
                Volatile.Write(ref _failure,null);
                _state=new() {TraceObserved=TraceObserved};_stop=new();var state=_state;var stop=_stop;
                _thread=new Thread(()=>RunOwned(state,stop)) {IsBackground=true,Name="Independent compatible glass backdrop"};
                _thread.SetApartmentState(ApartmentState.STA);_thread.Start();
            }
            try {_state.Request(new(region.X,region.Y,region.Right,region.Bottom),width,height);}
            catch(ObjectDisposedException)
            {TraceObserved?.Invoke(new("Consumer","WorkerStopped",Stopwatch.GetTimestamp()));return false;}
            // Published pixels are immutable and owned by this frame. The presentation
            // boundary takes its one snapshot only when the actual pixels change.
            return _state.TryRead(Stopwatch.GetTimestamp(),Stopwatch.Frequency*120/1000,out frame);
        }
    }
    private void RunOwned(LatestBackdropState state,CancellationTokenSource stop)
    {
        try {_run(state,stop.Token);}
        catch(Exception error) {Volatile.Write(ref _failure,error.Message);Trace.WriteLine("Compatible sample worker failed: "+error.Message);}
        finally
        {
            state.Dispose();
            lock(_gate)
            {
                if (!_stopping && Failure is not null) _policy.RecordFailure(Stopwatch.GetTimestamp());
                stop.Dispose();
                if(ReferenceEquals(_stop,stop)) _stop=null;
            }
        }
    }
    internal void Suspend()
    {
        // Retain the carrier, device and WGC session. The owner releases readback
        // scratch buffers after a long pause; quick toggles keep those warm too.
        // Resume advances the generation, rejecting hidden or in-flight results.
        lock(_gate) _state?.Suspend();
    }
    private void Run(LatestBackdropState state,CancellationToken stop)
    {
        IDesktopSceneCapture? capture=null;PixelBounds bounds=default;long sequence=0,idleSince=0,firstFrameDeadline=0;
        var signalGeneration=BackdropRecovery.Generation;
        var idleBuffersReleased=false;
        nint previousDpi=0;
        try
        {
            // Requests and WGC textures are physical pixels, like the native benchmark.
            previousDpi=SetThreadDpiAwarenessContext(-4);
            if(previousDpi==0) throw new InvalidOperationException("Physical capture DPI context unavailable.");
            using var resize=new NativeFrameResize();using var wait=new NativeCadenceWait();
            while(!stop.IsCancellationRequested)
            {
                if (signalGeneration != BackdropRecovery.Generation)
                {
                    signalGeneration = BackdropRecovery.Generation;
                    capture?.Dispose(); capture=null; bounds=default; resize.Dispose(); state.InvalidateScene();
                    TraceObserved?.Invoke(new("Recovery", "ResourcesRetired", Stopwatch.GetTimestamp()));
                }
                var request=state.CurrentRequest;
                if(request is null)
                {
                    if(capture is not null && !idleBuffersReleased)
                    {
                        if(idleSince==0) idleSince=Stopwatch.GetTimestamp();
                        if(Stopwatch.GetElapsedTime(idleSince)>=IdleBufferRetention)
                        {
                            // All native access stays on this worker. Keep completed
                            // presentation textures on the UI, avoiding an empty reveal.
                            capture.ReleaseReadbackBuffers();resize.Dispose();
                            idleBuffersReleased=true;
                            TraceObserved?.Invoke(new("CaptureBuffers","Released",Stopwatch.GetTimestamp()));
                        }
                    }
                    stop.WaitHandle.WaitOne(5);continue;
                }
                idleSince=0;idleBuffersReleased=false;
                var start=Stopwatch.GetTimestamp();
                if(capture is null || !Contains(bounds,request.Region))
                {
                    // Reserve the small gap from resting Dock capture to the physical
                    // display edge, as well as DIP rounding. Requests still crop their
                    // original pixels; this changes native capacity, not material padding.
                    var guarded=new PixelBounds(request.Region.Left-64,request.Region.Top-64,request.Region.Right+64,request.Region.Bottom+64);
                    if(DisplayService.Primary()?.Bounds is { } primary && primary.X<=request.Region.Left && primary.Y<=request.Region.Top
                        && primary.Right>=request.Region.Right && primary.Bottom>=request.Region.Bottom)
                        guarded=new(Math.Max(guarded.Left,primary.X),Math.Max(guarded.Top,primary.Y),
                            Math.Min(guarded.Right,primary.Right),Math.Min(guarded.Bottom,primary.Bottom));
                    capture?.Dispose();bounds=capture is null ? guarded : new(
                        Math.Min(bounds.Left,guarded.Left),Math.Min(bounds.Top,guarded.Top),
                        Math.Max(bounds.Right,guarded.Right),Math.Max(bounds.Bottom,guarded.Bottom));
                    capture=_create(new(bounds.Left,bounds.Top,bounds.Width,bounds.Height),
                        DiagnosticCarrierOnScreen,TraceObserved);
                    firstFrameDeadline=Stopwatch.GetTimestamp()+Stopwatch.Frequency*6;
                }
                var updateStart=Stopwatch.GetTimestamp();
                if (capture.UpdateExclusions()) state.InvalidateScene();
                TraceObserved?.Invoke(new("CaptureCost","SceneUpdate",Stopwatch.GetTimestamp(),EndTicks:updateStart));
                request=state.CurrentRequest;
                if (request is null) continue;
                CaptureStarting?.Invoke(capture.CarrierHandle);
                var sceneRevision=capture.SceneRevision;
                var readStart=Stopwatch.GetTimestamp();
                var frame=capture.TryRead(TimeSpan.FromMilliseconds(Math.Max(0,CaptureCadenceMilliseconds-Stopwatch.GetElapsedTime(start).TotalMilliseconds)),stop,request.SourceNotBeforeTicks,
                    reusePixels: CaptureObserved is null);
                TraceObserved?.Invoke(new("CaptureCost","FrameAcquisition",Stopwatch.GetTimestamp(),EndTicks:readStart));
                if (capture.SceneRevision != sceneRevision) state.InvalidateScene();
                if(frame is not null) { firstFrameDeadline=0; lock(_gate) _policy.RecordSuccess(); }
                else if(firstFrameDeadline!=0 && Stopwatch.GetTimestamp()>firstFrameDeadline)
                    throw new InvalidOperationException("Desktop background capture did not produce an initial frame.");
                // Motion can change the request during acquisition. Select the latest
                // coordinates from this retained carrier before resizing/publication.
                request=state.CurrentRequest;
                if(frame is not null && request is not null && Contains(bounds,request.Region))
                {
                    var processingStart=Stopwatch.GetTimestamp();
                    var crop=new PixelBounds(request.Region.Left-bounds.Left,request.Region.Top-bounds.Top,
                        request.Region.Right-bounds.Left,request.Region.Bottom-bounds.Top);
                    var sampled=resize.Resize(frame,request.Width,request.Height,crop);
                    TraceObserved?.Invoke(new("Processing","ResizeCompleted",Stopwatch.GetTimestamp(),frame.SourceQpcTicks,EndTicks:processingStart));
                    CaptureObserved?.Invoke(frame,sampled);
                    var now=Stopwatch.GetTimestamp();
                    var leadMs=(frame.SourceQpcTicks-now)*1000d/Stopwatch.Frequency;
                    // Keep the previous usable frame until the new compositor time arrives.
                    // This is a clock barrier on the worker, not a fixed settling delay.
                    if(leadMs>33)
                        TraceObserved?.Invoke(new("SourceClock","BeyondCadence",now,frame.SourceQpcTicks));
                    else
                    {
                        if(leadMs>0)
                        {
                            TraceObserved?.Invoke(new("SourceClock","Waiting",now,frame.SourceQpcTicks));
                            while(!stop.IsCancellationRequested && (now=Stopwatch.GetTimestamp())<frame.SourceQpcTicks)
                                wait.Wait((frame.SourceQpcTicks-now)*1000d/Stopwatch.Frequency,stop);
                            TraceObserved?.Invoke(new("SourceClock",stop.IsCancellationRequested ? "Cancelled" : "Reached",
                                Stopwatch.GetTimestamp(),frame.SourceQpcTicks));
                        }
                        if(!stop.IsCancellationRequested)
                            state.Publish(BackdropFrame.FromOwnedPixels(request,++sequence,start,Stopwatch.GetTimestamp(),sampled,
                                frame.SourceQpcTicks==0 ? null : frame.SourceQpcTicks, frame.IsStatic, frame.Source));
                    }
                }
                Program.Pump(TimeSpan.FromMilliseconds(1));
                var remaining=CaptureCadenceMilliseconds-Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if(remaining>0) wait.Wait(remaining,stop);
            }
        }
        catch(Exception error) {Volatile.Write(ref _failure,error.Message);Trace.WriteLine("Compatible sample source failed: "+error.Message);state.Dispose();}
        finally {capture?.Dispose();if(previousDpi!=0) SetThreadDpiAwarenessContext(previousDpi);}
    }
    private static bool Contains(PixelBounds outer,PixelBounds inner) =>
        outer.Left<=inner.Left && outer.Top<=inner.Top && outer.Right>=inner.Right && outer.Bottom>=inner.Bottom;
    public void Dispose()
    {
        lock(_gate)
        {
            _state?.Dispose();_stopping=true;_stop?.Cancel();
            // Visibility/unload callbacks run on the Dispatcher: request cancellation
            // without waiting for native work. The owning worker releases its CTS.
            if(_thread?.IsAlive!=true) {_thread=null;_state=null;_stop=null;}
        }
    }
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
}

internal sealed unsafe class NativeFrameResize : IDisposable
{
    private nint _sourceDc,_destDc,_sourceBitmap,_destBitmap,_sourceOld,_destOld,_sourceBits,_destBits;
    private (int,int,int,int) _size;
    private PixelBounds _crop;
    private byte[]? _lastPixels;
    internal byte[] Resize(CapturePixels frame,int width,int height)
        => Resize(frame,width,height,new PixelBounds(0,0,frame.Width,frame.Height));
    internal byte[] Resize(CapturePixels frame,int width,int height,PixelBounds crop)
    {
        if(frame.Width<=0 || frame.Height<=0 || width<=0 || height<=0 || frame.Pixels.Length!=(long)frame.Width*frame.Height*4)
            throw new ArgumentException("Invalid sample resize extent.");
        if(crop.Left<0 || crop.Top<0 || crop.Right>frame.Width || crop.Bottom>frame.Height || crop.Width<=0 || crop.Height<=0)
            throw new ArgumentOutOfRangeException(nameof(crop));
        var size=(frame.Width,frame.Height,width,height);
        if(_size!=size)
        {
            Dispose();_sourceDc=CreateCompatibleDC(0);_destDc=CreateCompatibleDC(0);
            var source=new Info {Size=40,Width=frame.Width,Height=-frame.Height,Planes=1,Bits=32};
            var dest=new Info {Size=40,Width=width,Height=-height,Planes=1,Bits=32};
            _sourceBitmap=CreateDIBSection(_sourceDc,ref source,0,out _sourceBits,0,0);
            _destBitmap=CreateDIBSection(_destDc,ref dest,0,out _destBits,0,0);
            if(_sourceDc==0 || _destDc==0 || _sourceBitmap==0 || _destBitmap==0 || _sourceBits==0 || _destBits==0)
                throw new InvalidOperationException("Sample resize allocation failed.");
            _sourceOld=SelectObject(_sourceDc,_sourceBitmap);_destOld=SelectObject(_destDc,_destBitmap);
            if(SetStretchBltMode(_destDc,3)==0) throw new InvalidOperationException("Sample resize mode failed.");
            _size=size;
        }
        // Compare against our retained native DIB, which still contains the previous
        // raw frame even when the capture worker reuses its CPU buffer. Crop changes
        // must resample; identical output remains immutable across publications.
        if(_lastPixels is not null && _crop==crop
            && frame.Pixels.AsSpan().SequenceEqual(new ReadOnlySpan<byte>((void*)_sourceBits,frame.Pixels.Length)))
            return _lastPixels;
        Marshal.Copy(frame.Pixels,0,_sourceBits,frame.Pixels.Length);
        if(!StretchBlt(_destDc,0,0,width,height,_sourceDc,crop.Left,crop.Top,crop.Width,crop.Height,0x00CC0020))
            throw new InvalidOperationException("Sample resize failed.");
        GdiFlush();var pixels=new byte[checked(width*height*4)];Marshal.Copy(_destBits,pixels,0,pixels.Length);
        for(var i=3;i<pixels.Length;i+=4) pixels[i]=255;
        _crop=crop;return _lastPixels=pixels;
    }
    public void Dispose()
    {
        if(_sourceOld!=0) SelectObject(_sourceDc,_sourceOld);if(_destOld!=0) SelectObject(_destDc,_destOld);
        if(_sourceBitmap!=0) DeleteObject(_sourceBitmap);if(_destBitmap!=0) DeleteObject(_destBitmap);
        if(_sourceDc!=0) DeleteDC(_sourceDc);if(_destDc!=0) DeleteDC(_destDc);
        _sourceDc=_destDc=_sourceBitmap=_destBitmap=_sourceOld=_destOld=_sourceBits=_destBits=0;_size=default;
        _lastPixels=null;_crop=default;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Info {public uint Size;public int Width,Height;public ushort Planes,Bits;public uint Compression,ImageSize;public int XPels,YPels;public uint Colors,Important;}
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc,ref Info info,uint usage,out nint bits,nint section,uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc,nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(nint dc,int mode);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(nint destination,int x,int y,int width,int height,nint source,int sx,int sy,int sw,int sh,uint operation);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
}
