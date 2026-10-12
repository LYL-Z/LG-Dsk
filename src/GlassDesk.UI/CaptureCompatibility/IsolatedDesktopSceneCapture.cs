using System.Diagnostics;
using System.IO;
using System.Text;
using GlassDesk.UI;
using GlassDesk.UI.Controls;

/// <summary>Bounded transactions; all native scene ownership stays in the helper.</summary>
internal sealed class IsolatedDesktopSceneCapture : IDesktopSceneCapture
{
    private readonly Process _process;
    private readonly Stream _input, _output;
    private readonly ScreenRect _region;
    private readonly GlassBackdropSource _source;
    public bool AllowsStaticContent => true;
    public long SceneRevision { get; private set; }
    private SharedCapturePixels? _sharedPixels;
    private byte[]? _pixels;
    private bool _disposed;
    private long _sequence, _generation = 1, _lastBarrier;
    internal nint CarrierHandle { get; private set; }
    nint IDesktopSceneCapture.CarrierHandle => CarrierHandle;

    internal IsolatedDesktopSceneCapture(ScreenRect region, Action<CaptureTrace>? trace = null, string? executable = null)
        : this(region, Start(executable), GlassBackdropSource.LiveDesktop, trace) { }
    internal IsolatedDesktopSceneCapture(ScreenRect region, bool documented, Action<CaptureTrace>? trace, string? executable = null)
        : this(region, Start(executable), documented ? GlassBackdropSource.CompatibleScene : GlassBackdropSource.LiveDesktop, trace) { }
    internal IsolatedDesktopSceneCapture(ScreenRect region, GlassBackdropSource source, Action<CaptureTrace>? trace, string? executable = null)
        : this(region, Start(executable), source, trace) { }
    internal IsolatedDesktopSceneCapture(ScreenRect region, ProcessStartInfo start)
        : this(region, start, GlassBackdropSource.LiveDesktop, null) { }
    private static ProcessStartInfo Start(string? executable) => new(executable ?? Environment.ProcessPath
        ?? throw new InvalidOperationException("Cannot locate capture executable."));
    private IsolatedDesktopSceneCapture(ScreenRect region, ProcessStartInfo start, GlassBackdropSource source, Action<CaptureTrace>? trace)
    {
        if (region.Width <= 0 || region.Height <= 0 || (long)region.Width * region.Height > 32_000_000)
            throw new ArgumentOutOfRangeException(nameof(region));
        if (source is not (GlassBackdropSource.LiveDesktop or GlassBackdropSource.CompatibleScene or GlassBackdropSource.Wallpaper))
            throw new ArgumentOutOfRangeException(nameof(source));
        _region = region; _source = source;
        start.UseShellExecute = false; start.RedirectStandardInput = true; start.RedirectStandardOutput = true;
        start.CreateNoWindow = true; start.WindowStyle = ProcessWindowStyle.Hidden;
        start.ArgumentList.Add("--glass-backdrop-worker"); start.ArgumentList.Add(Environment.ProcessId.ToString());
        CaptureProcessFamily.EnsureRetirementConfirmed();
        _process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start capture worker.");
        CaptureProcessFamily.Register(_process.Id);
        _input = _process.StandardInput.BaseStream; _output = _process.StandardOutput.BaseStream;
        try
        {
            _sharedPixels = SharedCapturePixels.Create(checked(region.Width * region.Height * 4));
            using var deadline = Deadline();
            Send(writer =>
            {
                writer.Write(BackdropCaptureWorker.ProtocolMagic); writer.Write(BackdropCaptureWorker.ProtocolVersion);
                writer.Write(source == GlassBackdropSource.LiveDesktop ? (byte)0 : source == GlassBackdropSource.CompatibleScene ? (byte)1 : (byte)2);
                writer.Write(region.X); writer.Write(region.Y);
                writer.Write(region.Width); writer.Write(region.Height); writer.Write(_sharedPixels.Name); WriteFamily(writer);
            }, deadline.Token);
            if (Status(deadline.Token) != 1) throw new InvalidDataException("Capture worker did not initialize.");
            CarrierHandle = (nint)BitConverter.ToInt64(Read(8, deadline.Token));
            trace?.Invoke(new("BackdropProvider", source.ToString(), Stopwatch.GetTimestamp()));
        }
        catch { Dispose(); throw; }
    }
    internal bool UpdateExclusions()
    {
        using var deadline = Deadline(); var sequence = ++_sequence;
        Command(3, sequence, deadline.Token);
        var changed = Status(deadline.Token) == 1; ValidateReply(sequence, deadline.Token);
        if (changed) ++_generation;
        return changed;
    }
    bool IDesktopSceneCapture.UpdateExclusions() => UpdateExclusions();
    internal CapturePixels? TryRead(TimeSpan waitForFrame = default, CancellationToken cancellation = default,
        long sourceNotBeforeTicks = 0, bool reusePixels = false)
    {
        cancellation.ThrowIfCancellationRequested();
        if (sourceNotBeforeTicks > _lastBarrier) { _lastBarrier = sourceNotBeforeTicks; ++_generation; }
        using var deadline = Deadline(cancellation); var sequence = ++_sequence;
        Command(1, sequence, deadline.Token, writer =>
        { writer.Write(sourceNotBeforeTicks); writer.Write((int)Math.Clamp(waitForFrame.TotalMilliseconds, 0, 100)); }, cancellation);
        var status = Status(deadline.Token, cancellation); ValidateReply(sequence, deadline.Token, cancellation);
        if (status == 0) return null;
        var header = Read(20, deadline.Token, cancellation);
        var width = BitConverter.ToInt32(header, 0); var height = BitConverter.ToInt32(header, 4);
        var ticks = BitConverter.ToInt64(header, 8); var bytes = BitConverter.ToInt32(header, 16);
        if (width != _region.Width || height != _region.Height || bytes != (long)width * height * 4
            || ticks <= 0 || ticks < sourceNotBeforeTicks || ticks > Stopwatch.GetTimestamp() + Stopwatch.Frequency / 30)
            throw new InvalidDataException("Capture worker returned invalid geometry or source time.");
        var pixels = reusePixels ? _pixels ??= new byte[bytes] : new byte[bytes]; _sharedPixels!.Read(pixels);
        return new(width, height, pixels) { SourceQpcTicks = ticks, IsStatic = true,
            Source = _source };
    }
    CapturePixels? IDesktopSceneCapture.TryRead(TimeSpan wait, CancellationToken cancellation, long notBefore, bool reuse)
        => TryRead(wait, cancellation, notBefore, reuse);
    internal void ReleaseReadbackBuffers()
    {
        using var deadline = Deadline(); var sequence = ++_sequence;
        Command(2, sequence, deadline.Token); Status(deadline.Token); ValidateReply(sequence, deadline.Token); _pixels = null;
    }
    void IDesktopSceneCapture.ReleaseReadbackBuffers() => ReleaseReadbackBuffers();
    private CancellationTokenSource Deadline(CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(3)); return deadline;
    }
    private void Command(byte command, long sequence, CancellationToken token, Action<BinaryWriter>? payload = null,
        CancellationToken caller = default)
        => Send(writer =>
        { writer.Write(command); writer.Write(sequence); writer.Write(_generation); WriteFamily(writer); payload?.Invoke(writer); }, token, caller);
    private static void WriteFamily(BinaryWriter writer)
    {
        var family = CaptureProcessFamily.Snapshot(); writer.Write(family.Length);
        foreach (var pid in family) writer.Write(pid);
    }
    private void Send(Action<BinaryWriter> write, CancellationToken token, CancellationToken caller = default)
    {
        using var message = new MemoryStream();
        using (var writer = new BinaryWriter(message, Encoding.UTF8, true)) write(writer);
        try
        {
            _input.WriteAsync(message.GetBuffer().AsMemory(0, (int)message.Length), token).AsTask().GetAwaiter().GetResult();
            _input.FlushAsync(token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        { throw new TimeoutException("Background capture request timed out."); }
    }
    private void ValidateReply(long sequence, CancellationToken token, CancellationToken cancellation = default)
    {
        var identity = Read(24, token, cancellation);
        if (BitConverter.ToInt64(identity, 0) != sequence || BitConverter.ToInt64(identity, 8) != _generation)
            throw new InvalidDataException("Capture worker returned a stale request or scene generation.");
        SceneRevision = BitConverter.ToInt64(identity, 16);
    }
    private byte Status(CancellationToken token, CancellationToken cancellation = default)
    {
        var status = Read(1, token, cancellation)[0];
        if (status == 2)
        {
            var length = BitConverter.ToInt32(Read(4, token, cancellation));
            if (length < 0 || length > 8192) throw new InvalidDataException("Invalid capture error length.");
            throw new InvalidOperationException("Isolated capture failed: " + Encoding.UTF8.GetString(Read(length, token, cancellation)));
        }
        if (status > 2) throw new InvalidDataException("Invalid capture worker response.");
        return status;
    }
    private byte[] Read(int bytes, CancellationToken token, CancellationToken cancellation = default)
    {
        var buffer = new byte[bytes];
        try { _output.ReadExactlyAsync(buffer, token).AsTask().GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new TimeoutException("Background capture worker stopped responding."); }
        return buffer;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Runs on the feed's worker, never the Dispatcher. Exit precedes mapping retirement.
        try
        {
            try { if (!_process.HasExited) _process.Kill(); }
            catch (InvalidOperationException) when (_process.HasExited) { }
            if (!_process.WaitForExit(1000)) throw new TimeoutException("Capture worker exit was not confirmed.");
        }
        catch
        {
            CaptureProcessFamily.Quarantine(_process.Id);
            _ = Task.Run(async () =>
            {
                // Retain this owner and mapping until OS exit confirmation. New native
                // sessions stay gated while the independently drawn UI remains usable.
                await _process.WaitForExitAsync(); ReleaseExitedResources();
            });
            throw;
        }
        ReleaseExitedResources();
    }
    private void ReleaseExitedResources()
    {
        CaptureProcessFamily.ConfirmRetirement(_process.Id);
        _input.Dispose(); _output.Dispose(); _process.Dispose(); _pixels = null;
        _sharedPixels?.Dispose(); _sharedPixels = null; CarrierHandle = 0;
    }
}
