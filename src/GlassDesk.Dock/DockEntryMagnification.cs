namespace GlassDesk.Dock;

/// <summary>
/// Reusable target/interpolation state for Dock groups rendered by WPF Canvas.
/// The geometry returned here is also the geometry used for hit testing.
/// </summary>
internal sealed class DockEntryMagnification
{
    private TileGeometry[] _painted = [];
    private TileGeometry[] _targets = [];

    internal ReadOnlySpan<TileGeometry> Geometry => _painted;

    internal void Reset(int count, double iconSize, double spacing, double peak)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        EnsureCapacity(count);
        DockMagnification.ComputeInto(_painted, iconSize, spacing, peak, null);
        Array.Copy(_painted, _targets, count);
    }

    internal bool Advance(double iconSize, double spacing, double peak, double? pointerPosition,
        double deltaSeconds, bool animationsEnabled)
    {
        DockMagnification.ComputeInto(_targets, iconSize, spacing, peak, pointerPosition);
        var allSettled = true;

        for (var index = 0; index < _painted.Length; index++)
        {
            var current = _painted[index];
            var target = _targets[index];
            var scale = animationsEnabled
                ? DockHoverScale.Advance(current.Scale, target.Scale, deltaSeconds)
                : target.Scale;
            var center = animationsEnabled
                ? DockHoverScale.Advance(current.Center, target.Center, deltaSeconds)
                : target.Center;

            _painted[index] = new TileGeometry(center, scale);
            allSettled &= scale == target.Scale && center == target.Center;
        }

        return allSettled;
    }

    internal int HitTest(double iconSize, double pointerPosition) =>
        DockMagnification.HitTest(_painted, iconSize, pointerPosition);

    private void EnsureCapacity(int count)
    {
        if (_painted.Length == count) return;
        _painted = new TileGeometry[count];
        _targets = new TileGeometry[count];
    }
}
