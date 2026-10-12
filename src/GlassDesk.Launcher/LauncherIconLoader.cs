using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows.Media.Imaging;

namespace GlassDesk.Launcher;

// File decoding and Shell extraction belong to one background STA, never paging's
// Dispatcher. Completed frozen images can be shared by all pages and groups.
internal sealed class LauncherIconLoader : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Task<BitmapImage?>> _cache = new(StringComparer.Ordinal);
    private readonly BlockingCollection<Work> _queue = new(512);
    private readonly Thread _thread;
    private bool _disposed;
    private sealed record Work(Func<BitmapImage?> Resolve, TaskCompletionSource<BitmapImage?> Completion);

    internal LauncherIconLoader()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "GlassDesk launcher icons" };
        _thread.SetApartmentState(ApartmentState.STA); _thread.Start();
    }
    internal Task<BitmapImage?> LoadAsync(string key, Func<BitmapImage?> resolve)
    {
        lock (_gate)
        {
            if (_disposed) return Task.FromResult<BitmapImage?>(null);
            if (_cache.TryGetValue(key, out var cached)) return cached;
            // Do not grow a decoded-image cache with every page in a large library.
            while (_cache.Count >= 256)
            {
                var completed = _cache.FirstOrDefault(pair => pair.Value.IsCompleted);
                if (completed.Key is null) break;
                _cache.Remove(completed.Key);
            }
            var completion = new TaskCompletionSource<BitmapImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_queue.TryAdd(new(resolve, completion))) return Task.FromResult<BitmapImage?>(null);
            _cache[key] = completion.Task;
            return completion.Task;
        }
    }
    internal void Clear() { lock (_gate) _cache.Clear(); }
    private void Run()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            lock (_gate)
            {
                if (_disposed) { work.Completion.TrySetResult(null); continue; }
            }
            try { work.Completion.TrySetResult(work.Resolve()); }
            catch (Exception error)
            {
                Trace.WriteLine("Launcher icon unavailable: " + error.Message);
                work.Completion.TrySetResult(null);
            }
        }
        lock (_gate) _queue.Dispose();
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _cache.Clear(); _queue.CompleteAdding();
            // A Shell extension may be blocked. Never join it from Window.Closed.
            while (_queue.TryTake(out var work)) work.Completion.TrySetResult(null);
        }
    }
}
