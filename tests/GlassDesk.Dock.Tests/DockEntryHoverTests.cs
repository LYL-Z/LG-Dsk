namespace GlassDesk.Dock.Tests;

public sealed class DockEntryHoverTests
{
    [Theory]
    [InlineData(48d, 8d, 1.15d)]
    [InlineData(48d, 8d, 1.35d)]
    [InlineData(72d, 5d, 1.2d)]
    public void FixedAndRecentEntriesReflowAndHitTestThePaintedFrame(
        double iconSize, double spacing, double peak)
    {
        const int count = 5;
        var pitch = iconSize + spacing;
        var layout = new DockEntryMagnification();
        layout.Reset(count, iconSize, spacing, peak);

        var resting = layout.Geometry.ToArray();
        for (var index = 1; index < count; index++)
            Assert.Equal(pitch, resting[index].Center - resting[index - 1].Center, 9);

        var pointer = DockMagnification.SlotCenter(2, iconSize, spacing, peak);
        for (var frame = 0; frame < 300; frame++)
            layout.Advance(iconSize, spacing, peak, pointer, 1d / 60, animationsEnabled: true);

        var painted = layout.Geometry.ToArray();
        Assert.Equal(peak, painted[2].Scale, 3);
        Assert.True(painted[1].Scale > 1, "The neighboring icon should join the continuous wave.");
        Assert.True(painted[3].Scale > 1, "Both neighbors should respond to the pointer.");
        Assert.True(painted[1].Center < resting[1].Center,
            "The enlarged center icon must make room by moving its left neighbor outward.");
        Assert.True(painted[3].Center > resting[3].Center,
            "The enlarged center icon must make room by moving its right neighbor outward.");

        for (var index = 0; index < count - 1; index++)
        {
            var rightEdge = painted[index].Center + iconSize * painted[index].Scale / 2;
            var nextLeftEdge = painted[index + 1].Center - iconSize * painted[index + 1].Scale / 2;
            Assert.Equal(spacing, nextLeftEdge - rightEdge, 6);
            Assert.Equal(index, layout.HitTest(iconSize, rightEdge - .01));
            Assert.Equal(-1, layout.HitTest(iconSize, (rightEdge + nextLeftEdge) / 2));
        }
        Assert.Equal(2, layout.HitTest(iconSize, painted[2].Center));

        for (var frame = 0; frame < 300; frame++)
            layout.Advance(iconSize, spacing, peak, null, 1d / 60, animationsEnabled: true);

        var returned = layout.Geometry.ToArray();
        Assert.Equal(resting, returned);
    }

    [Fact]
    public void RapidPointerTravelKeepsNeighborGapsAndHitTargetsStable()
    {
        const double iconSize = 48;
        const double spacing = 8;
        const double peak = 1.35;
        const int count = 8;
        var layout = new DockEntryMagnification();
        layout.Reset(count, iconSize, spacing, peak);

        foreach (var destination in new[] { 0, count - 1, 1, count - 2, count / 2 })
        {
            var pointer = DockMagnification.SlotCenter(destination, iconSize, spacing, peak);
            for (var frame = 0; frame < 8; frame++)
            {
                layout.Advance(iconSize, spacing, peak, pointer, 1d / 120, animationsEnabled: true);
                var painted = layout.Geometry.ToArray();
                for (var index = 0; index < count; index++)
                {
                    Assert.InRange(painted[index].Scale, 1, peak);
                    Assert.Equal(index, layout.HitTest(iconSize, painted[index].Center));
                    if (index == count - 1) continue;
                    var rightEdge = painted[index].Center + iconSize * painted[index].Scale / 2;
                    var nextLeftEdge = painted[index + 1].Center - iconSize * painted[index + 1].Scale / 2;
                    Assert.InRange(nextLeftEdge - rightEdge, spacing - .1, spacing + .1);
                }
            }
        }
    }
}
