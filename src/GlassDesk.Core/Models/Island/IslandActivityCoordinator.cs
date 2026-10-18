namespace GlassDesk.Core.Models.Island;

/// <summary>A renderable activity selected by priority from the island activity set.</summary>
public sealed record IslandActivityPresentation(
    string Key,
    string Text,
    int Priority,
    bool IsTransient,
    TimeSpan? Remaining);

/// <summary>
/// Coalesces repeated transient events, arbitrates them against persistent activities,
/// and restores the next activity when a higher-priority event expires.
/// Expiration uses TimeProvider timestamps (monotonic), not wall-clock time.
/// </summary>
public sealed class IslandActivityCoordinator
{
    public static readonly TimeSpan MaximumTransientDuration = TimeSpan.FromHours(1);

    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, ActivityEntry> _entries = new(StringComparer.Ordinal);
    private long _sequence;

    public IslandActivityCoordinator(TimeProvider? timeProvider = null)
        => _timeProvider = timeProvider ?? TimeProvider.System;

    public void SetPersistent(string key, string text, int priority)
    {
        Validate(key, text);
        lock (_gate)
        {
            _entries[key] = new(key, text, priority, ++_sequence, null, null);
        }
    }

    public bool RemovePersistent(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            return _entries.TryGetValue(key, out var entry) && !entry.IsTransient && _entries.Remove(key);
        }
    }

    public bool Remove(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate) return _entries.Remove(key);
    }

    /// <summary>Publishes or refreshes an event with the same key instead of duplicating it.</summary>
    public void PublishTransient(string key, string text, int priority, TimeSpan duration)
    {
        Validate(key, text);
        if (duration <= TimeSpan.Zero || duration > MaximumTransientDuration)
            throw new ArgumentOutOfRangeException(nameof(duration));

        lock (_gate)
        {
            _entries[key] = new(key, text, priority, ++_sequence, _timeProvider.GetTimestamp(), duration);
        }
    }

    public IslandActivityPresentation? ReadCurrent()
    {
        lock (_gate)
        {
            var now = _timeProvider.GetTimestamp();
            RemoveExpired(now);
            var selected = _entries.Values
                .OrderByDescending(entry => entry.Priority)
                .ThenByDescending(entry => entry.Sequence)
                .FirstOrDefault();
            if (selected is null) return null;

            TimeSpan? remaining = selected.IsTransient
                ? selected.Duration!.Value - _timeProvider.GetElapsedTime(selected.StartTimestamp!.Value, now)
                : null;
            return new(selected.Key, selected.Text, selected.Priority, selected.IsTransient, remaining);
        }
    }

    public TimeSpan? TimeUntilNextExpiration()
    {
        lock (_gate)
        {
            var now = _timeProvider.GetTimestamp();
            RemoveExpired(now);
            var expirations = _entries.Values
                .Where(entry => entry.IsTransient)
                .Select(entry => entry.Duration!.Value - _timeProvider.GetElapsedTime(entry.StartTimestamp!.Value, now))
                .ToArray();
            if (expirations.Length == 0) return null;
            var earliest = expirations.Min();
            return earliest <= TimeSpan.Zero ? TimeSpan.Zero : earliest;
        }
    }

    private void RemoveExpired(long now)
    {
        foreach (var entry in _entries.Values.Where(entry => entry.IsExpired(_timeProvider, now)).ToArray())
            _entries.Remove(entry.Key);
    }

    private static void Validate(string key, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
    }

    private sealed record ActivityEntry(
        string Key,
        string Text,
        int Priority,
        long Sequence,
        long? StartTimestamp,
        TimeSpan? Duration)
    {
        public bool IsTransient => StartTimestamp.HasValue && Duration.HasValue;

        public bool IsExpired(TimeProvider timeProvider, long now)
            => IsTransient && timeProvider.GetElapsedTime(StartTimestamp!.Value, now) >= Duration!.Value;
    }
}
