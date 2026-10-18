using System.Runtime.InteropServices;
using System.Threading;

namespace GlassDesk.Platform.Audio;

/// <summary>A read-only snapshot of the current default render endpoint's master volume.</summary>
public sealed record EndpointVolumeSnapshot(bool IsAvailable, int Percent, bool IsMuted, string? DeviceId)
{
    public static EndpointVolumeSnapshot Unavailable => new(false, 0, false, null);

    public string ToIslandLabel() => !IsAvailable
        ? "音量状态不可用"
        : $"{(IsMuted ? "静音" : "音量")} · {(IsMuted ? "—" : $"{Math.Clamp(Percent, 0, 100)}%")}";
}

/// <summary>
/// Observes the Windows default render endpoint through Core Audio callbacks.
/// This class never changes the system volume and never polls volume values.
/// </summary>
public sealed class CoreAudioEndpointMonitor : IDisposable
{
    private readonly AutoResetEvent _reconnect = new(false);
    private readonly AutoResetEvent _volumeChanged = new(false);
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly object _snapshotGate = new();
    private readonly object _lifetimeGate = new();
    private readonly Thread _worker;
    private EndpointVolumeSnapshot _current = EndpointVolumeSnapshot.Unavailable;
    private EndpointVolumeSnapshot? _pendingVolume;
    private volatile bool _disposed;
    private bool _disposeSignalsOnExit;
    private bool _signalsDisposed;
    private int _disposeStarted;

    /// <summary>Raised only for an actual endpoint volume/mute notification, not initial discovery.</summary>
    public event Action<EndpointVolumeSnapshot>? VolumeChanged;

    public EndpointVolumeSnapshot Current
    {
        get { lock (_snapshotGate) return _current; }
    }

    public CoreAudioEndpointMonitor(Action<EndpointVolumeSnapshot>? onVolumeChanged = null)
    {
        if (onVolumeChanged is not null) VolumeChanged += onVolumeChanged;
        _worker = new Thread(WorkerMain)
        {
            IsBackground = true,
            Name = "LG Dsk Core Audio observer",
        };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
    }

    private void WorkerMain()
    {
        var initialized = CoInitializeEx(0, 0) >= 0;
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioEndpointVolume? endpoint = null;
        EndpointVolumeCallback? volumeCallback = null;
        DeviceNotificationCallback? deviceCallback = null;
        var notificationRegistered = false;
        var volumeRegistered = false;

        try
        {
            var waitHandles = new WaitHandle[] { _stop.WaitHandle, _reconnect, _volumeChanged };
            while (!_stop.IsSet)
            {
                try
                {
                    var enumeratorType = Type.GetTypeFromCLSID(ClassMmDeviceEnumerator, throwOnError: true)!;
                    enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType)!;
                    deviceCallback = new DeviceNotificationCallback(SignalReconnect);
                    CheckHResult(enumerator.RegisterEndpointNotificationCallback(deviceCallback));
                    notificationRegistered = true;

                    CheckHResult(enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out device));
                    var endpointId = ReadDeviceId(device);
                    var iid = IidAudioEndpointVolume;
                    CheckHResult(device.Activate(ref iid, ClsCtxAll, 0, out endpoint));

                    CheckHResult(endpoint.GetMasterVolumeLevelScalar(out var scalar));
                    CheckHResult(endpoint.GetMute(out var muted));
                    Publish(new(true, ToPercent(scalar), muted, endpointId));

                    volumeCallback = new EndpointVolumeCallback(snapshot =>
                        QueueVolumeSnapshot(snapshot with { DeviceId = endpointId }));
                    CheckHResult(endpoint.RegisterControlChangeNotify(volumeCallback));
                    volumeRegistered = true;

                    // Keep COM registration and release on this MTA owner thread. The
                    // Core Audio callback only stores the latest value and signals it.
                    while (!_stop.IsSet)
                    {
                        var signal = WaitHandle.WaitAny(waitHandles);
                        if (signal is 0 or 1) break;
                        if (signal == 2 && TakePendingVolume() is { } snapshot)
                            Publish(snapshot, isVolumeNotification: true);
                    }
                }
                catch (Exception ex) when (IsRecoverable(ex))
                {
                    Publish(EndpointVolumeSnapshot.Unavailable);
                    _ = WaitHandle.WaitAny(waitHandles, TimeSpan.FromSeconds(3));
                }
                finally
                {
                    Interlocked.Exchange(ref _pendingVolume, null);
                    if (volumeRegistered && endpoint is not null && volumeCallback is not null)
                    {
                        try { _ = endpoint.UnregisterControlChangeNotify(volumeCallback); }
                        catch (Exception ex) when (IsRecoverable(ex)) { }
                    }
                    volumeRegistered = false;
                    volumeCallback = null;

                    if (notificationRegistered && enumerator is not null && deviceCallback is not null)
                    {
                        try { _ = enumerator.UnregisterEndpointNotificationCallback(deviceCallback); }
                        catch (Exception ex) when (IsRecoverable(ex)) { }
                    }
                    notificationRegistered = false;
                    deviceCallback = null;
                    ReleaseComObject(endpoint); endpoint = null;
                    ReleaseComObject(device); device = null;
                    ReleaseComObject(enumerator); enumerator = null;
                }
            }
        }
        finally
        {
            if (initialized) CoUninitialize();
            Publish(EndpointVolumeSnapshot.Unavailable);
            DisposeSignalsIfRequested();
        }
    }

    private void QueueVolumeSnapshot(EndpointVolumeSnapshot snapshot)
    {
        if (_disposed) return;
        Interlocked.Exchange(ref _pendingVolume, snapshot);
        try { _volumeChanged.Set(); }
        catch (ObjectDisposedException) { }
    }

    private EndpointVolumeSnapshot? TakePendingVolume()
        => Interlocked.Exchange(ref _pendingVolume, null);

    private void SignalReconnect()
    {
        try { _reconnect.Set(); }
        catch (ObjectDisposedException) { }
    }

    private void Publish(EndpointVolumeSnapshot snapshot, bool isVolumeNotification = false)
    {
        if (_disposed) return;
        lock (_snapshotGate)
        {
            if (_current == snapshot) return;
            _current = snapshot;
        }

        if (!isVolumeNotification) return;

        try { VolumeChanged?.Invoke(snapshot); }
        catch (Exception)
        {
            // A presentation subscriber must never terminate the COM owner thread.
        }
    }

    private static string? ReadDeviceId(IMMDevice device)
    {
        CheckHResult(device.GetId(out var value));
        try { return Marshal.PtrToStringUni(value); }
        finally { Marshal.FreeCoTaskMem(value); }
    }

    private static int ToPercent(float scalar)
        => (int)Math.Clamp(Math.Round(Math.Clamp(scalar, 0, 1) * 100, MidpointRounding.AwayFromZero), 0, 100);

    private static void CheckHResult(int result)
    {
        if (result < 0) Marshal.ThrowExceptionForHR(result);
    }

    private static bool IsRecoverable(Exception exception)
        => exception is COMException or InvalidCastException or InvalidOperationException
            or DllNotFoundException or EntryPointNotFoundException or TypeLoadException;

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try { _ = Marshal.FinalReleaseComObject(value); }
            catch (InvalidComObjectException) { }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        _disposed = true;
        try { _stop.Set(); } catch (ObjectDisposedException) { }
        SignalReconnect();
        try { _volumeChanged.Set(); } catch (ObjectDisposedException) { }

        if (Thread.CurrentThread != _worker) _ = _worker.Join(TimeSpan.FromSeconds(3));
        lock (_lifetimeGate)
        {
            _disposeSignalsOnExit = true;
            if (!_worker.IsAlive) DisposeSignalsUnsafe();
        }
    }

    private void DisposeSignalsIfRequested()
    {
        lock (_lifetimeGate)
        {
            if (_disposeSignalsOnExit) DisposeSignalsUnsafe();
        }
    }

    private void DisposeSignalsUnsafe()
    {
        if (_signalsDisposed) return;
        _signalsDisposed = true;
        _reconnect.Dispose();
        _volumeChanged.Dispose();
        _stop.Dispose();
    }

    private static readonly Guid ClassMmDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private const int ClsCtxAll = 23;

    private enum DataFlow { Render, Capture, All }
    private enum Role { Console, Multimedia, Communications }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(DataFlow flow, uint stateMask, out nint devices);
        [PreserveSig] int GetDefaultAudioEndpoint(DataFlow flow, Role role,
            [MarshalAs(UnmanagedType.Interface)] out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id,
            [MarshalAs(UnmanagedType.Interface)] out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(
            [MarshalAs(UnmanagedType.Interface)] DeviceNotificationCallback callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(
            [MarshalAs(UnmanagedType.Interface)] DeviceNotificationCallback callback);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int classContext, nint activationParameters,
            [MarshalAs(UnmanagedType.Interface)] out IAudioEndpointVolume endpoint);
        [PreserveSig] int OpenPropertyStore(uint access, out nint properties);
        [PreserveSig] int GetId(out nint id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify([MarshalAs(UnmanagedType.Interface)] EndpointVolumeCallback callback);
        [PreserveSig] int UnregisterControlChangeNotify([MarshalAs(UnmanagedType.Interface)] EndpointVolumeCallback callback);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint stepCount);
        [PreserveSig] int VolumeStepUp(ref Guid eventContext);
        [PreserveSig] int VolumeStepDown(ref Guid eventContext);
        [PreserveSig] int QueryHardwareSupport(out uint hardwareSupportMask);
        [PreserveSig] int GetVolumeRange(out float minDb, out float maxDb, out float incrementDb);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    [Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
    private sealed class EndpointVolumeCallback(Action<EndpointVolumeSnapshot> publish) : IAudioEndpointVolumeCallback
    {
        public int OnNotify(nint notification)
        {
            try
            {
                var data = Marshal.PtrToStructure<AudioVolumeNotificationData>(notification);
                publish(new(true, ToPercent(data.MasterVolume), data.Muted != 0, null));
            }
            catch (Exception)
            {
                // The callback is deliberately best-effort and never throws into COM.
            }
            return 0;
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    [Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
    private sealed class DeviceNotificationCallback(Action changed) : IMMNotificationClient
    {
        public int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, uint newState) { changed(); return 0; }
        public int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId) { changed(); return 0; }
        public int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId) { changed(); return 0; }
        public int OnDefaultDeviceChanged(DataFlow flow, Role role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId)
        { if (flow == DataFlow.Render) changed(); return 0; }
        public int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref PropertyKey key)
        {
            // The monitor reads no endpoint properties; volume and mute have their own
            // callback, so unrelated property traffic must not force a reconnect.
            return 0;
        }
    }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(nint notification);
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, uint newState);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int OnDefaultDeviceChanged(DataFlow flow, Role role,
            [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref PropertyKey key);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid FormatId; public uint PropertyId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioVolumeNotificationData
    {
        public Guid EventContext;
        public int Muted;
        public float MasterVolume;
        public uint ChannelCount;
        public float FirstChannelVolume;
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(nint reserved, uint coInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();
}
