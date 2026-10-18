using GlassDesk.Core.Models.Island;

namespace GlassDesk.Core.Tests;

public sealed class IslandTimerCoordinatorTests
{
    [Fact]
    public void CountsDownFromMonotonicElapsedTimeAndReportsCompletionOnce()
    {
        var clock = new ManualTimeProvider();
        var coordinator = new IslandTimerCoordinator(clock);
        var id = coordinator.Start("专注", TimeSpan.FromMinutes(2));

        clock.Advance(TimeSpan.FromSeconds(75));

        var active = Assert.Single(coordinator.ReadAll());
        Assert.Equal(id, active.Id);
        Assert.Equal(TimeSpan.FromSeconds(45), active.Remaining);
        Assert.Empty(coordinator.CollectCompleted());

        clock.Advance(TimeSpan.FromSeconds(45));
        var completed = Assert.Single(coordinator.CollectCompleted());
        Assert.Equal(id, completed.Id);
        Assert.Equal("专注", completed.Name);
        Assert.Empty(coordinator.CollectCompleted());
        Assert.Empty(coordinator.ReadAll());
    }

    [Fact]
    public void PauseAndResumePreserveRemainingDuration()
    {
        var clock = new ManualTimeProvider();
        var coordinator = new IslandTimerCoordinator(clock);
        coordinator.Start("休息", TimeSpan.FromSeconds(90));
        clock.Advance(TimeSpan.FromSeconds(30));
        coordinator.PauseAll();
        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(TimeSpan.FromSeconds(60), Assert.Single(coordinator.ReadAll()).Remaining);
        Assert.False(Assert.Single(coordinator.ReadAll()).IsRunning);

        coordinator.ResumeAll();
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromSeconds(50), Assert.Single(coordinator.ReadAll()).Remaining);
    }

    [Fact]
    public void PausingAnElapsedTimerDoesNotLoseItsCompletion()
    {
        var clock = new ManualTimeProvider();
        var coordinator = new IslandTimerCoordinator(clock);
        coordinator.Start("已到期", TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(2));

        coordinator.PauseAll();

        Assert.Equal("已到期", Assert.Single(coordinator.CollectCompleted()).Name);
    }

    [Fact]
    public void SupportsSeveralTimersWithDeterministicOrderAndBoundedCount()
    {
        var coordinator = new IslandTimerCoordinator(new ManualTimeProvider());
        for (var i = 0; i < IslandTimerCoordinator.MaximumTimerCount; i++)
            coordinator.Start($"Timer {i}", TimeSpan.FromMinutes(i + 1));

        Assert.Equal(IslandTimerCoordinator.MaximumTimerCount, coordinator.ReadAll().Count);
        Assert.Throws<InvalidOperationException>(() => coordinator.Start("overflow", TimeSpan.FromMinutes(1)));
        coordinator.CancelAll();
        Assert.Empty(coordinator.ReadAll());
    }

    [Fact]
    public void RejectsInvalidTimerNamesAndDurations()
    {
        var coordinator = new IslandTimerCoordinator();

        Assert.Throws<ArgumentException>(() => coordinator.Start(" ", TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => coordinator.Start("zero", TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => coordinator.Start(
            "too long", IslandTimerCoordinator.MaximumDuration.Add(TimeSpan.FromTicks(1))));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }
}
