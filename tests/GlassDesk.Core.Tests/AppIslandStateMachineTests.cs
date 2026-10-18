using GlassDesk.Core.Models.Island;

namespace GlassDesk.Core.Tests;

public sealed class AppIslandStateMachineTests
{
    [Fact]
    public void ContextEntersCompactAndLeavingItAlwaysHidesAndClearsPin()
    {
        var state = new AppIslandStateMachine();

        Assert.True(state.SetContextAvailable(true));
        Assert.Equal(AppIslandState.Compact, state.State);
        Assert.True(state.TogglePinned());
        Assert.Equal(AppIslandState.PinnedExpanded, state.State);

        Assert.True(state.SetContextAvailable(false));
        Assert.Equal(AppIslandState.Hidden, state.State);
        Assert.False(state.IsPinned);
        Assert.False(state.PointerEntered());
    }

    [Fact]
    public void HoverExpansionWaitsForGracePeriodAndDoesNotCollapseWhilePointerIsInside()
    {
        var clock = new ManualTimeProvider();
        var state = new AppIslandStateMachine(timeProvider: clock);
        state.SetContextAvailable(true);
        Assert.True(state.PointerEntered());
        Assert.Equal(AppIslandState.HoverExpanded, state.State);

        Assert.False(state.PointerLeft());
        clock.Advance(TimeSpan.FromMilliseconds(699));
        Assert.False(state.Advance());
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(state.Advance());
        Assert.Equal(AppIslandState.Compact, state.State);
    }

    [Fact]
    public void ReenteringDuringCollapseCancelsDeadlineAndClickPinsExpansion()
    {
        var clock = new ManualTimeProvider();
        var state = new AppIslandStateMachine(timeProvider: clock);
        state.SetContextAvailable(true);
        state.PointerEntered();
        state.PointerLeft();
        clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.False(state.Advance());
        Assert.False(state.PointerEntered());
        Assert.False(state.HasPendingCollapse);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(state.Advance());
        Assert.Equal(AppIslandState.HoverExpanded, state.State);

        Assert.True(state.TogglePinned());
        Assert.Equal(AppIslandState.PinnedExpanded, state.State);
        state.PointerLeft();
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.False(state.Advance());
        Assert.True(state.DismissExpansion());
        Assert.Equal(AppIslandState.Compact, state.State);
    }

    [Fact]
    public void CollapseDelayMustStayWithinSafeRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AppIslandStateMachine(TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AppIslandStateMachine(TimeSpan.FromSeconds(11)));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }
}
