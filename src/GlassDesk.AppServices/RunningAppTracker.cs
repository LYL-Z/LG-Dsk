using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Threading;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using GlassDesk.Platform;
using GlassDesk.Platform.Shell;

namespace GlassDesk.AppServices;

/// <summary>
/// 跟踪「哪些记录此刻有已运行的窗口」，供运行状态指示点（D-07）使用。
/// </summary>
/// <remarks>
/// <para>
/// 结构是<strong>初始枚举 + 事件触发 + 调和</strong>三件套，刻意不把
/// WinEvent 事件当成状态的唯一来源：
/// </para>
/// <list type="number">
/// <item><b>初始枚举</b>：<see cref="Start"/> 时立即枚举一遍窗口——Dock 启动之前
/// 就在运行的应用（占绝大多数）只有这样才能被看见；</item>
/// <item><b>事件</b>：<see cref="WindowEventWatcher"/> 只当触发信号，回调只入队，
/// 合并后的重算不发生在钩子线程上（官方文档明确提醒回调有重入风险）；</item>
/// <item><b>调和</b>：收到通知后<strong>重新枚举</strong>窗口集合，以枚举结果为准。
/// 单靠 CREATE/DESTROY 无法覆盖「隐藏后重新显示」「由其他宿主代开窗口」这类情形，
/// 枚举天然覆盖。</item>
/// </list>
///
/// <para>
/// 窗口态与进程态分开采样。窗口事件负责及时更新可切换窗口；每 5 秒只查询
/// 应用库里可确认的可执行文件名，补足仍在运行但没有可匹配窗口的经典桌面进程。
/// 身份不足或进程路径不可读时保留 <see cref="AppRuntimeState.Unknown"/>，不将共享宿主
/// 或同名进程误认成目标应用。进程扫描不参与悬停动画，也会随性能休眠暂停。
/// </para>
/// </remarks>
public sealed class RunningAppTracker : IDisposable
{
    /// <summary>事件合并后仍然要调和的最小间隔。事件风暴被 WindowEventWatcher 压平，这里不再重复。</summary>
    private static readonly TimeSpan MinimumReconcileInterval = TimeSpan.FromMilliseconds(150);
    private const int ProcessRefreshIntervalMilliseconds = 5000;

    private readonly AppLibraryStore _library;
    private readonly ConcurrentDictionary<string, string?> _identityCache = new();
    private readonly Lock _sync = new();
    private readonly Timer _processRefreshTimer;

    private WindowEventWatcher? _watcher;
    private IReadOnlyList<WindowIdentityInfo> _windows = [];
    private IReadOnlyDictionary<string, AppRuntimeState> _processStates =
        new Dictionary<string, AppRuntimeState>(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastReconcileUtc = DateTime.MinValue;
    private volatile bool _disposed;
    private bool _suspended, _dirty;
    private long _runtimeGeneration, _reconcileCount;
    private int _processScanRunning;
    private string? _lastForegroundAppId;

    /// <summary>Completed enumeration count, useful for resource-lifecycle diagnostics.</summary>
    public long ReconcileCount => Interlocked.Read(ref _reconcileCount);
    public bool IsSuspended { get { lock (_sync) return _suspended; } }

    public void SetSuspended(bool suspended)
    {
        bool reconcile;
        lock (_sync)
        {
            if (_disposed || _suspended == suspended) return;
            _suspended = suspended;
            _runtimeGeneration++;
            reconcile = !suspended && _dirty;
            if (reconcile) _lastReconcileUtc = DateTime.MinValue;
        }
        _processRefreshTimer.Change(
            suspended ? Timeout.Infinite : 0,
            suspended ? Timeout.Infinite : ProcessRefreshIntervalMilliseconds);
        if (reconcile) Reconcile();
    }

    public RunningAppTracker(AppLibraryStore library)
    {
        ArgumentNullException.ThrowIfNull(library);
        _library = library;
        _processRefreshTimer = new Timer(
            static state => ((RunningAppTracker)state!).RefreshProcessSnapshot(),
            this,
            Timeout.Infinite,
            Timeout.Infinite);
    }

    /// <summary>
    /// 运行状态有变化（可能）时触发。在线程池线程上触发，
    /// 更新界面的监听方自行切回自己的线程。
    /// </summary>
    /// <remarks>
    /// 事件是「可能变了」的信号：调和被合并与跳过时不会发。
    /// 监听方应当在收到后<strong>重新读取</strong>状态，而不是假设知道哪里变了——
    /// 这与「以枚举为准」是同一条原则在通知侧的体现。
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>Raised after a foreground window uniquely matches an application library entry.</summary>
    public event EventHandler<string>? ForegroundApplicationChanged;

    /// <summary>最近一次调和得到的候选窗口集合。诊断与测试用。</summary>
    public IReadOnlyList<WindowIdentityInfo> LastWindows
    {
        get
        {
            lock (_sync)
            {
                return _windows;
            }
        }
    }

    /// <summary>
    /// 启动跟踪：先做初始枚举，再装事件钩子。
    /// </summary>
    /// <remarks>
    /// 钩子装不上（理论上只发生在入口点缺失的异常环境）不算失败：
    /// 状态退化为「只有初始枚举时的快照」，调用方仍然可以按需
    /// 调 <see cref="Reconcile"/> 刷新。
    /// </remarks>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Reconcile();

        _watcher = WindowEventWatcher.TryStart(
            onChanged: () => _ = Reconcile(),
            onForegroundWindowChanged: OnForegroundWindowChanged);
        if (!IsSuspended)
            _processRefreshTimer.Change(0, ProcessRefreshIntervalMilliseconds);
    }

    private void OnForegroundWindowChanged(nint handle)
    {
        if (_disposed) return;

        var window = LastWindows.FirstOrDefault(candidate => candidate.Handle == handle);
        if (window is null)
        {
            _ = ReconcileOnDemand();
            window = LastWindows.FirstOrDefault(candidate => candidate.Handle == handle);
        }

        var entry = WindowMatcher.FindUniqueEntryForWindow(_library.Items, window, _identityCache);
        if (entry is null)
        {
            _ = Interlocked.Exchange(ref _lastForegroundAppId, null);
            return;
        }

        var previous = Interlocked.Exchange(ref _lastForegroundAppId, entry.Id);
        if (string.Equals(previous, entry.Id, StringComparison.Ordinal)) return;
        ForegroundApplicationChanged?.Invoke(this, entry.Id);
    }

    /// <summary>Re-evaluates the current foreground window after a consumer subscribes.</summary>
    public void RefreshForegroundApplication()
    {
        if (_disposed) return;
        OnForegroundWindowChanged(WindowControl.GetForegroundWindow());
    }

    /// <summary>立即重新枚举并刷新状态；返回本次枚举到的窗口数。</summary>
    public int Reconcile()
        => ReconcileCore(force: false, allowWhileSuspended: false);

    /// <summary>
    /// Refreshes the window snapshot in response to an explicit user request or while the
    /// lightweight fullscreen application island is visible. It bypasses event throttling
    /// without changing the tracker's suspension policy.
    /// </summary>
    public int ReconcileOnDemand()
        => ReconcileCore(force: true, allowWhileSuspended: true);

    private int ReconcileCore(bool force, bool allowWhileSuspended)
    {
        if (_disposed)
        {
            return 0;
        }

        long generation;
        lock (_sync)
        {
            if (_suspended && !allowWhileSuspended) { _dirty = true; return _windows.Count; }
            // 与上次调和贴得太近就跳过：事件合并（120 毫秒）之后仍可能
            // 有成串的通知，重复的全量枚举没有意义。
            if (!force && DateTime.UtcNow - _lastReconcileUtc < MinimumReconcileInterval)
            {
                return _windows.Count;
            }

            _lastReconcileUtc = DateTime.UtcNow;
            generation = _runtimeGeneration;
        }

        var windows = WindowLocator.EnumerateTopLevelWindows();
        bool changed;

        lock (_sync)
        {
            if (_disposed || _suspended && !allowWhileSuspended || generation != _runtimeGeneration)
            { _dirty = true; return _windows.Count; }
            _dirty = false;
            _reconcileCount++;
            changed = !SameWindowSet(_windows, windows);
            _windows = windows;
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return windows.Count;
    }

    /// <summary>读取应用的进程/窗口状态；快照读取不触发系统枚举。</summary>
    public AppRuntimeState GetRuntimeState(AppEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Kind is not (AppItemKind.Program or AppItemKind.Shortcut))
            return AppRuntimeState.NotApplicable;

        IReadOnlyList<WindowIdentityInfo> windows;
        IReadOnlyDictionary<string, AppRuntimeState> processStates;
        lock (_sync)
        {
            windows = _windows;
            processStates = _processStates;
        }

        var aumid = WindowMatcher.ResolveAumid(entry, _identityCache);
        if (WindowMatcher.MatchWindows(entry, windows, aumid).Count > 0)
            return AppRuntimeState.RunningWithWindows;
        return GetProcessState(entry, processStates);
    }

    /// <summary>条目是否已运行；状态未知时返回 false，但不会把未知显示成已停止。</summary>
    public bool IsRunning(AppEntry entry)
        => GetRuntimeState(entry) is AppRuntimeState.RunningWithWindows or AppRuntimeState.RunningWithoutWindow;

    /// <summary>这条记录此刻匹配的已运行窗口（对最近一次调和的快照匹配）。规则见 <see cref="WindowMatcher"/>。</summary>
    public IReadOnlyList<WindowIdentityInfo> FindWindows(AppEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return WindowMatcher.MatchWindows(entry, LastWindows, WindowMatcher.ResolveAumid(entry, _identityCache));
    }

    /// <summary>
    /// 给定候选里此刻有窗口在运行的记录 id。
    /// </summary>
    /// <remarks>
    /// 以调用方给出的候选集合为准（Dock 只关心它显示的那些条目），
    /// 不自行扫描全库——500 条里绝大多数不在 Dock 上，为它们逐条
    /// 判定是在浪费每次调和的时间。
    /// </remarks>
    public IReadOnlyList<string> FindRunningIds(IEnumerable<AppEntry> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        IReadOnlyList<WindowIdentityInfo> windows;
        IReadOnlyDictionary<string, AppRuntimeState> processStates;
        lock (_sync)
        {
            windows = _windows;
            processStates = _processStates;
        }

        var running = new List<string>();
        foreach (var entry in candidates)
        {
            // AppLibraryStore intentionally retains malformed/null rows so users can
            // repair them; they must not abort the running-state query for valid apps.
            if (entry is null || !entry.IsUsable) continue;
            var state = entry.Kind is not (AppItemKind.Program or AppItemKind.Shortcut)
                ? AppRuntimeState.NotApplicable
                : WindowMatcher.MatchWindows(
                    entry, windows, WindowMatcher.ResolveAumid(entry, _identityCache)).Count > 0
                    ? AppRuntimeState.RunningWithWindows
                    : GetProcessState(entry, processStates);
            if (state is AppRuntimeState.RunningWithWindows or AppRuntimeState.RunningWithoutWindow)
                running.Add(entry.Id);
        }
        return running;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watcher?.Dispose();
        _watcher = null;
        _processRefreshTimer.Dispose();
    }

    private void RefreshProcessSnapshot()
    {
        if (_disposed || Interlocked.CompareExchange(ref _processScanRunning, 1, 0) != 0) return;
        try
        {
            lock (_sync)
                if (_disposed || _suspended) return;

            var next = CaptureProcessStates();
            bool changed;
            lock (_sync)
            {
                if (_disposed || _suspended) return;
                changed = !SameProcessStates(_processStates, next);
                if (changed) _processStates = next;
            }

            if (changed) Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A process can exit or deny module access during enumeration. Keep the
            // previous immutable snapshot and retry on the next low-rate tick.
            Trace.TraceWarning("Process-state refresh failed: {0}", ex.GetType().Name);
        }
        finally
        {
            Volatile.Write(ref _processScanRunning, 0);
        }
    }

    private Dictionary<string, AppRuntimeState> CaptureProcessStates()
    {
        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _library.Items)
        {
            if (entry is null || !entry.IsUsable) continue;
            if (entry.Kind is not (AppItemKind.Program or AppItemKind.Shortcut)
                || !WindowMatcher.CanMatchByImage(entry)) continue;
            if (!TryGetProcessTarget(entry.Target, out var path, out var processName)) continue;
            targets.TryAdd(path, processName);
        }

        var targetsByProcessName = targets
            .GroupBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        var runningPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unknownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (targetsByProcessName.Count == 0) return new Dictionary<string, AppRuntimeState>(StringComparer.OrdinalIgnoreCase);

        Process[] processes;
        try
        {
            // Take one process-table snapshot per low-rate refresh. Calling
            // GetProcessesByName once per installed app can repeatedly enumerate the
            // entire Windows process table on machines with large app libraries.
            processes = Process.GetProcesses();
        }
        catch (Exception ex) when (IsProcessInspectionException(ex))
        {
            return targets.ToDictionary(
                pair => pair.Key,
                _ => AppRuntimeState.Unknown,
                StringComparer.OrdinalIgnoreCase);
        }

        var allTargetNames = targetsByProcessName.Keys.ToArray();
        foreach (var process in processes)
        {
            using (process)
            {
                string processName;
                try
                {
                    processName = process.ProcessName;
                }
                catch (InvalidOperationException)
                {
                    // The process exited after the system snapshot was taken.
                    continue;
                }
                catch (Exception ex) when (IsProcessInspectionException(ex))
                {
                    // ProcessName can be unavailable for protected processes. Try the
                    // least-privilege image-path query before deciding whether any target
                    // is affected.
                    string? fallbackPath = null;
                    try { fallbackPath = WindowLocator.TryReadImagePath(checked((uint)process.Id)); }
                    catch (Exception fallbackException) when (IsProcessInspectionException(fallbackException)) { }
                    if (!string.IsNullOrWhiteSpace(fallbackPath))
                    {
                        var normalizedFallback = NormalizeProcessPath(fallbackPath);
                        if (targets.ContainsKey(normalizedFallback)) runningPaths.Add(normalizedFallback);
                        continue;
                    }

                    // Without either a process name or image path, attribution is unsafe.
                    unknownNames.UnionWith(allTargetNames);
                    continue;
                }

                if (!targetsByProcessName.TryGetValue(processName, out var candidatePaths)) continue;

                try
                {
                    var imagePath = WindowLocator.TryReadImagePath(checked((uint)process.Id));
                    if (string.IsNullOrWhiteSpace(imagePath))
                    {
                        unknownNames.Add(processName);
                        continue;
                    }

                    var normalized = NormalizeProcessPath(imagePath);
                    if (candidatePaths.Contains(normalized)) runningPaths.Add(normalized);
                }
                catch (InvalidOperationException)
                {
                    // This process exited while the path was being read.
                }
                catch (Exception ex) when (IsProcessInspectionException(ex))
                {
                    unknownNames.Add(processName);
                }
            }
        }

        var snapshot = new Dictionary<string, AppRuntimeState>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, processName) in targets)
        {
            var state = runningPaths.Contains(path)
                ? AppRuntimeState.RunningWithoutWindow
                : unknownNames.Contains(processName)
                    ? AppRuntimeState.Unknown
                    : AppRuntimeState.Stopped;
            snapshot[path] = state;
        }
        return snapshot;
    }

    private static AppRuntimeState GetProcessState(
        AppEntry entry,
        IReadOnlyDictionary<string, AppRuntimeState> processStates)
    {
        if (entry.Kind is AppItemKind.Folder or AppItemKind.File or AppItemKind.Url)
            return AppRuntimeState.NotApplicable;
        if (!WindowMatcher.CanMatchByImage(entry)
            || !TryGetProcessTarget(entry.Target, out var path, out _))
            return AppRuntimeState.Unknown;
        return processStates.TryGetValue(path, out var state) ? state : AppRuntimeState.Unknown;
    }

    private static bool TryGetProcessTarget(string path, out string normalizedPath, out string processName)
    {
        normalizedPath = string.Empty;
        processName = string.Empty;
        try
        {
            normalizedPath = NormalizeProcessPath(path);
            processName = Path.GetFileNameWithoutExtension(normalizedPath);
            return !string.IsNullOrWhiteSpace(normalizedPath) && !string.IsNullOrWhiteSpace(processName);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string NormalizeProcessPath(string path)
    {
        var trimmed = path.Trim();
        try { return Path.GetFullPath(trimmed); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return trimmed; }
    }

    private static bool IsProcessInspectionException(Exception exception)
        => exception is Win32Exception or InvalidOperationException or NotSupportedException
            or SecurityException or UnauthorizedAccessException or ArgumentException;

    private static bool SameProcessStates(
        IReadOnlyDictionary<string, AppRuntimeState> left,
        Dictionary<string, AppRuntimeState> right)
    {
        if (left.Count != right.Count) return false;
        foreach (var (path, state) in left)
            if (!right.TryGetValue(path, out var other) || other != state) return false;
        return true;
    }

    /// <summary>
    /// 两组窗口是否等价。只比较句柄集合：窗口的存在与消失才改变
    /// 「有没有在运行」，同一窗口改标题或挪位置不算。
    /// </summary>
    private static bool SameWindowSet(IReadOnlyList<WindowIdentityInfo> left, IReadOnlyList<WindowIdentityInfo> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        var handles = new HashSet<nint>(left.Select(window => window.Handle));

        foreach (var window in right)
        {
            if (!handles.Remove(window.Handle))
            {
                return false;
            }
        }

        return handles.Count == 0;
    }
}
