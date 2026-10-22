namespace GlassDesk.Dock.Tests;

public sealed class DockSlideInterpolatorTests
{
    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(240)]
    public void SameElapsedTimeProducesRefreshRateIndependentProgress(int refreshRate)
    {
        var current = 0d;
        var elapsed = TimeSpan.FromMilliseconds(100);
        var frameCount = (int)Math.Round(refreshRate * elapsed.TotalSeconds);
        var frameTime = TimeSpan.FromSeconds(1d / refreshRate);
        for (var frame = 0; frame < frameCount; frame++)
            current = DockSlideInterpolator.Advance(current, 400, frameTime);

        var expected = 400 * (1 - Math.Exp(-elapsed.TotalSeconds / .055));
        Assert.InRange(Math.Abs(current - expected), 0, 1.0);
    }

    [Fact]
    public void InterpolationIsMonotonicAndClampsLongFramePauses()
    {
        var first = DockSlideInterpolator.Advance(0, 100, TimeSpan.FromMilliseconds(16));
        var resumed = DockSlideInterpolator.Advance(first, 100, TimeSpan.FromSeconds(2));

        Assert.InRange(first, 0, 100);
        Assert.InRange(resumed, first, 100);
        Assert.True(resumed < 100);
    }

    [Fact]
    public void ZeroDurationDoesNotMoveTheSlide()
        => Assert.Equal(0, DockSlideInterpolator.Advance(0, 100, TimeSpan.Zero));
}
