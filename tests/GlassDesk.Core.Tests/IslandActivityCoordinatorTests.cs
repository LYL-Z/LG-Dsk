using GlassDesk.Core.Models.Island;

namespace GlassDesk.Core.Tests;

public sealed class IslandActivityCoordinatorTests
{
    [Fact]
    public void HigherPriorityTransientWinsThenRestoresPersistentActivity()
    {
        var clock = new ManualTimeProvider();
        var coordinator = new IslandActivityCoordinator(clock);
        coordinator.SetPersistent("media", "正在播放 · 示例曲目", priority: 10);
        coordinator.PublishTransient("volume", "音量 · 42%", priority: 50, TimeSpan.FromSeconds(2));

        Assert.Equal("volume", coordinator.ReadCurrent()!.Key);
        Assert.Equal(TimeSpan.FromSeconds(2), coordinator.TimeUntilNextExpiration());

        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal("media", coordinator.ReadCurrent()!.Key);
        Assert.Null(coordinator.TimeUntilNextExpiration());
    }

    [Fact]
    public void HigherPriorityTimerCanPreemptAndResumeTheCurrentVolumeFeedback()
    {
        var clock = new ManualTimeProvider();
        var coordinator = new IslandActivityCoordinator(clock);
        coordinator.PublishTransient("volume", "音量 · 42%", priority: 20, TimeSpan.FromSeconds(3));
        clock.Advance(TimeSpan.FromMilliseconds(500));
        coordinator.PublishTransient("timer", "计时结束", priority: 100, TimeSpan.FromSeconds(1));

        Assert.Equal("timer", coordinator.ReadCurrent()!.Key);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("volume", coordinator.ReadCurrent()!.Key);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), coordinator.ReadCurrent()!.Remaining);
    }

    [Fact]
    public void RepeatedEventsWithSameKeyCoalesceAndRefreshTheirLifetime()
    {
        var clock = new ManualTimeProvider();
        var coordinator = new IslandActivityCoordinator(clock);
        coordinator.PublishTransient("volume", "音量 · 42%", priority: 20, TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromSeconds(1));
        coordinator.PublishTransient("volume", "音量 · 58%", priority: 20, TimeSpan.FromSeconds(2));

        var current = coordinator.ReadCurrent()!;
        Assert.Equal("音量 · 58%", current.Text);
        Assert.Equal(TimeSpan.FromSeconds(2), current.Remaining);
        Assert.Equal(TimeSpan.FromSeconds(2), coordinator.TimeUntilNextExpiration());
    }

    [Fact]
    public void RejectsUnboundedOrEmptyActivities()
    {
        var coordinator = new IslandActivityCoordinator();

        Assert.Throws<ArgumentException>(() => coordinator.SetPersistent("media", " ", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => coordinator.PublishTransient(
            "volume", "音量", 1, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => coordinator.PublishTransient(
            "volume", "音量", 1, IslandActivityCoordinator.MaximumTransientDuration.Add(TimeSpan.FromTicks(1))));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }
}
