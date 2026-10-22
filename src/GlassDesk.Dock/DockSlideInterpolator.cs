namespace GlassDesk.Dock;

/// <summary>Time-based critically damped-style slide interpolation, independent of refresh rate.</summary>
public static class DockSlideInterpolator
{
    private const double TimeConstantSeconds = 0.055;
    private const double MaximumStepSeconds = 0.050;
    private const double SettledTolerancePixels = 0.5;

    public static double Advance(double current, double target, TimeSpan elapsed)
    {
        if (!double.IsFinite(current) || !double.IsFinite(target))
            throw new ArgumentOutOfRangeException(nameof(current), "Slide coordinates must be finite.");
        if (elapsed <= TimeSpan.Zero) return current;

        var seconds = Math.Min(elapsed.TotalSeconds, MaximumStepSeconds);
        var progress = 1 - Math.Exp(-seconds / TimeConstantSeconds);
        var next = current + (target - current) * progress;
        return Math.Abs(next - target) <= SettledTolerancePixels ? target : next;
    }
}
