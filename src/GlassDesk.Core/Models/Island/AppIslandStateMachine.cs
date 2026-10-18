namespace GlassDesk.Core.Models.Island;

/// <summary>The interaction state of LG Dsk's compact taskbar island.</summary>
public enum AppIslandState
{
    Hidden,
    Compact,
    HoverExpanded,
    PinnedExpanded,
}

/// <summary>
/// Owns the island's visibility and expansion transitions independently of WPF timers.
/// The Dock renders this state; it does not infer state from animation or window size.
/// </summary>
public sealed class AppIslandStateMachine
{
    public static readonly TimeSpan DefaultCollapseDelay = TimeSpan.FromMilliseconds(700);

    private readonly TimeSpan _collapseDelay;
    private readonly TimeProvider _timeProvider;
    private long? _collapseStartedAt;
    private bool _pointerInside;
    private bool _contextAvailable;

    public AppIslandState State { get; private set; } = AppIslandState.Hidden;
    public bool IsVisible => State != AppIslandState.Hidden;
    public bool IsExpanded => State is AppIslandState.HoverExpanded or AppIslandState.PinnedExpanded;
    public bool IsPinned => State == AppIslandState.PinnedExpanded;
    public bool IsPointerInside => _pointerInside;
    public bool HasPendingCollapse => _collapseStartedAt.HasValue;

    public AppIslandStateMachine(TimeSpan? collapseDelay = null, TimeProvider? timeProvider = null)
    {
        _collapseDelay = collapseDelay ?? DefaultCollapseDelay;
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (_collapseDelay < TimeSpan.Zero || _collapseDelay > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(collapseDelay));
    }

    /// <summary>Sets whether the operating context calls for the island to be available.</summary>
    public bool SetContextAvailable(bool available)
    {
        _contextAvailable = available;
        if (!available)
        {
            _pointerInside = false;
            _collapseStartedAt = null;
            return Transition(AppIslandState.Hidden);
        }

        if (State == AppIslandState.Hidden)
            return Transition(AppIslandState.Compact);

        return false;
    }

    public bool PointerEntered()
    {
        if (!_contextAvailable) return false;
        _pointerInside = true;
        _collapseStartedAt = null;
        return State != AppIslandState.PinnedExpanded && Transition(AppIslandState.HoverExpanded);
    }

    public bool PointerLeft()
    {
        _pointerInside = false;
        if (State != AppIslandState.HoverExpanded)
        {
            _collapseStartedAt = null;
            return false;
        }

        _collapseStartedAt = _timeProvider.GetTimestamp();
        return false;
    }

    /// <summary>Click pins the expanded panel; a second click returns it to compact mode.</summary>
    public bool TogglePinned()
    {
        if (!_contextAvailable) return false;
        _collapseStartedAt = null;
        return Transition(State == AppIslandState.PinnedExpanded
            ? AppIslandState.Compact
            : AppIslandState.PinnedExpanded);
    }

    public bool DismissExpansion()
    {
        if (!_contextAvailable || !IsExpanded) return false;
        _collapseStartedAt = null;
        return Transition(AppIslandState.Compact);
    }

    /// <summary>Advances delayed hover collapse using a monotonic clock.</summary>
    public bool Advance()
    {
        if (State != AppIslandState.HoverExpanded || _pointerInside || _collapseStartedAt is not { } startedAt
            || _timeProvider.GetElapsedTime(startedAt, _timeProvider.GetTimestamp()) < _collapseDelay)
            return false;

        _collapseStartedAt = null;
        return Transition(AppIslandState.Compact);
    }

    private bool Transition(AppIslandState next)
    {
        if (State == next) return false;
        State = next;
        return true;
    }
}
