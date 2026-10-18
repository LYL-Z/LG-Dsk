namespace GlassDesk.Core.Models.Island;

public sealed record IslandTimerSnapshot(string Id, string Name, TimeSpan Remaining, bool IsRunning);
public sealed record IslandTimerCompletion(string Id, string Name);

/// <summary>
/// Owns session-local countdowns using monotonic timestamps. UI ticks only sample this
/// state; elapsed time is never calculated by decrementing a frame counter.
/// </summary>
public sealed class IslandTimerCoordinator
{
    public const int MaximumTimerCount = 8;
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromDays(7);

    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, TimerEntry> _timers = new(StringComparer.Ordinal);
    private long _sequence;

    public IslandTimerCoordinator(TimeProvider? timeProvider = null)
        => _timeProvider = timeProvider ?? TimeProvider.System;

    public string Start(string name, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 80) throw new ArgumentOutOfRangeException(nameof(name));
        if (duration <= TimeSpan.Zero || duration > MaximumDuration)
            throw new ArgumentOutOfRangeException(nameof(duration));

        lock (_gate)
        {
            if (_timers.Count >= MaximumTimerCount)
                throw new InvalidOperationException($"最多同时运行 {MaximumTimerCount} 个计时器。");
            var id = Guid.NewGuid().ToString("N");
            _timers.Add(id, new(id, name.Trim(), duration, _timeProvider.GetTimestamp(), true, ++_sequence));
            return id;
        }
    }

    public IReadOnlyList<IslandTimerSnapshot> ReadAll()
    {
        lock (_gate)
        {
            var now = _timeProvider.GetTimestamp();
            return _timers.Values
                .OrderBy(entry => entry.Sequence)
                .Select(entry => new IslandTimerSnapshot(entry.Id, entry.Name, Remaining(entry, now), entry.IsRunning))
                .ToArray();
        }
    }

    public IReadOnlyList<IslandTimerCompletion> CollectCompleted()
    {
        lock (_gate)
        {
            var now = _timeProvider.GetTimestamp();
            var completed = _timers.Values
                .Where(entry => entry.IsRunning && Remaining(entry, now) <= TimeSpan.Zero)
                .OrderBy(entry => entry.Sequence)
                .Select(entry => new IslandTimerCompletion(entry.Id, entry.Name))
                .ToArray();
            foreach (var item in completed) _timers.Remove(item.Id);
            return completed;
        }
    }

    public void PauseAll()
    {
        lock (_gate)
        {
            var now = _timeProvider.GetTimestamp();
            foreach (var entry in _timers.Values.Where(entry => entry.IsRunning).ToArray())
            {
                var remaining = Remaining(entry, now);
                if (remaining <= TimeSpan.Zero) continue;
                _timers[entry.Id] = entry with { RemainingWhenPaused = remaining, IsRunning = false };
            }
        }
    }

    public void ResumeAll()
    {
        lock (_gate)
        {
            var now = _timeProvider.GetTimestamp();
            foreach (var entry in _timers.Values.Where(entry => !entry.IsRunning && entry.RemainingWhenPaused > TimeSpan.Zero).ToArray())
                _timers[entry.Id] = entry with { StartTimestamp = now, IsRunning = true };
        }
    }

    public void CancelAll()
    {
        lock (_gate) _timers.Clear();
    }

    private TimeSpan Remaining(TimerEntry entry, long now)
    {
        if (!entry.IsRunning) return entry.RemainingWhenPaused;
        var elapsed = _timeProvider.GetElapsedTime(entry.StartTimestamp, now);
        var remaining = entry.RemainingWhenPaused - elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private sealed record TimerEntry(
        string Id,
        string Name,
        TimeSpan RemainingWhenPaused,
        long StartTimestamp,
        bool IsRunning,
        long Sequence);
}
