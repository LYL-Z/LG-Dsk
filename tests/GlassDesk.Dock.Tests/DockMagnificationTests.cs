using GlassDock = GlassDesk.Dock.DockMagnification;

namespace GlassDesk.Dock.Tests;

/// <summary>连续放大、邻居让位和绘制/命中共用布局快照。</summary>
public sealed class DockMagnificationTests
{
    private const double Size = 48;
    private const double Spacing = 8;
    private const double Peak = 1.35;

    [Fact]
    public void 未放大时图标等距且在画布中居中()
    {
        var geometry = GlassDock.Compute(4, Size, Spacing, Peak, pointerPosition: null);
        Assert.All(geometry, tile => Assert.Equal(1, tile.Scale));
        for (var index = 0; index < geometry.Length; index++)
            Assert.Equal(GlassDock.SlotCenter(index, Size, Spacing, Peak), geometry[index].Center, 9);
    }

    [Fact]
    public void 放大时邻居让位且间距保持一致()
    {
        var pointers = new double?[]
        {
            null,
            GlassDock.SlotCenter(0, Size, Spacing, Peak),
            GlassDock.SlotCenter(2, Size, Spacing, Peak),
            (GlassDock.SlotCenter(1, Size, Spacing, Peak) + GlassDock.SlotCenter(2, Size, Spacing, Peak)) / 2,
            5_000,
        };

        foreach (var pointer in pointers)
        {
            var geometry = GlassDock.Compute(6, Size, Spacing, Peak, pointer);
            for (var index = 0; index < geometry.Length - 1; index++)
            {
                var rightEdge = geometry[index].Center + Size * geometry[index].Scale / 2;
                var nextLeftEdge = geometry[index + 1].Center - Size * geometry[index + 1].Scale / 2;
                Assert.Equal(Spacing, nextLeftEdge - rightEdge, 7);
            }
        }
    }

    [Fact]
    public void 每个图标都保持在固定画布边界内()
    {
        var count = 7;
        var length = GlassDock.RequiredLength(count, Size, Spacing, Peak);
        var pointers = Enumerable.Range(-20, 421).Select(step => (double?)(step * 5d))
            .Append(null);

        foreach (var pointer in pointers)
        {
            var geometry = GlassDock.Compute(count, Size, Spacing, Peak, pointer);
            for (var index = 0; index < count; index++)
            {
                var half = Size * geometry[index].Scale / 2;
                Assert.True(geometry[index].Center - half >= -0.001);
                Assert.True(geometry[index].Center + half <= length + 0.001);
            }
        }
    }

    [Fact]
    public void 绘制顺序与命中顺序共用同一份几何()
    {
        var geometry = GlassDock.Compute(5, Size, Spacing, Peak,
            GlassDock.SlotCenter(2, Size, Spacing, Peak) - 6);
        var order = GlassDock.TopToBottomOrder(geometry);

        for (var index = 1; index < order.Length; index++)
            Assert.True(geometry[order[index - 1]].Scale >= geometry[order[index]].Scale);

        foreach (var index in Enumerable.Range(0, geometry.Length))
        {
            var probe = geometry[index].Center;
            Assert.Equal(index, GlassDock.HitTest(geometry, Size, probe));
        }
    }

    [Fact]
    public void 放大峰值和相邻波形连续()
    {
        var centers = Enumerable.Range(0, 5)
            .Select(index => GlassDock.SlotCenter(index, Size, Spacing, Peak)).ToArray();
        var atCenter = GlassDock.Compute(5, Size, Spacing, Peak, centers[2]);
        Assert.Equal(Peak, atCenter[2].Scale, 9);
        Assert.Equal(1 + (Peak - 1) * .25, atCenter[1].Scale, 9);
        Assert.Equal(1 + (Peak - 1) * .25, atCenter[3].Scale, 9);
        Assert.Equal(1, atCenter[0].Scale, 9);
        Assert.Equal(1, atCenter[4].Scale, 9);

        var midpoint = GlassDock.Compute(5, Size, Spacing, Peak, (centers[1] + centers[2]) / 2);
        Assert.Equal(midpoint[1].Scale, midpoint[2].Scale, 9);
        Assert.InRange(midpoint[1].Scale, 1.01, Peak - .01);

        var left = GlassDock.Compute(5, Size, Spacing, Peak, centers[2] - 0.01);
        var right = GlassDock.Compute(5, Size, Spacing, Peak, centers[2] + 0.01);
        Assert.InRange(Math.Abs(left[2].Scale - right[2].Scale), 0, .001);
    }

    [Fact]
    public void 独立入口可复用同一条距离曲线并在邻居处连续衰减()
    {
        var pitch = Size + Spacing;
        Assert.Equal(Peak, GlassDock.ScaleAtDistance(0, Size, Spacing, Peak), 9);
        Assert.Equal(1 + (Peak - 1) * .25, GlassDock.ScaleAtDistance(pitch, Size, Spacing, Peak), 9);
        Assert.Equal(1, GlassDock.ScaleAtDistance(pitch * 1.5, Size, Spacing, Peak), 9);

        var before = GlassDock.ScaleAtDistance(pitch * 1.5 - .01, Size, Spacing, Peak);
        var after = GlassDock.ScaleAtDistance(pitch * 1.5 + .01, Size, Spacing, Peak);
        Assert.InRange(Math.Abs(before - after), 0, .001);
    }

    [Fact]
    public void 放大倍率可调()
    {
        var geometry = GlassDock.Compute(5, Size, Spacing, 2.0,
            GlassDock.SlotCenter(1, Size, Spacing, 2.0));
        Assert.Equal(2, geometry[1].Scale, 9);
        Assert.True(geometry[0].Center < geometry[1].Center);
    }

    [Fact]
    public void 点击使用当前可见几何且图标间隙不误命中()
    {
        var pointer = GlassDock.SlotCenter(2, Size, Spacing, Peak);
        var geometry = GlassDock.Compute(5, Size, Spacing, Peak, pointer);
        Assert.Equal(2, GlassDock.HitTest(geometry, Size, pointer));

        var gap = geometry[2].Center + Size * geometry[2].Scale / 2 + Spacing / 2;
        Assert.Equal(-1, GlassDock.HitTest(geometry, Size, gap));
    }

    [Fact]
    public void 总长度为基础宽度加连续波形的放大余量()
    {
        Assert.Equal(160, GlassDock.RequiredLength(3, Size, Spacing, 1));
        Assert.Equal(160 + Size * (Peak - 1) * 1.5, GlassDock.RequiredLength(3, Size, Spacing, Peak), 9);

        var length = GlassDock.RequiredLength(3, Size, Spacing, Peak);
        var resting = GlassDock.Compute(3, Size, Spacing, Peak, null);
        Assert.Equal(GlassDock.EndPadding(Size, Peak), resting[0].Center - Size / 2, 9);

        var atFirst = GlassDock.Compute(3, Size, Spacing, Peak,
            GlassDock.SlotCenter(0, Size, Spacing, Peak));
        var firstLeadingPadding = atFirst[0].Center - Size * atFirst[0].Scale / 2;
        var firstTrailingPadding = length - (atFirst[^1].Center + Size * atFirst[^1].Scale / 2);
        Assert.Equal(firstLeadingPadding, firstTrailingPadding, 7);

        var atLast = GlassDock.Compute(3, Size, Spacing, Peak,
            GlassDock.SlotCenter(2, Size, Spacing, Peak));
        var lastLeadingPadding = atLast[0].Center - Size * atLast[0].Scale / 2;
        var lastTrailingPadding = length - (atLast[^1].Center + Size * atLast[^1].Scale / 2);
        Assert.Equal(lastLeadingPadding, lastTrailingPadding, 7);
    }

    [Fact]
    public void 空列表和单图标都有合法几何()
    {
        Assert.Empty(GlassDock.Compute(0, Size, Spacing, Peak, null));
        var one = GlassDock.Compute(1, Size, Spacing, Peak, GlassDock.SlotCenter(0, Size, Spacing, Peak));
        Assert.Single(one);
        Assert.Equal(Peak, one[0].Scale, 9);
        Assert.Equal(Size + Size * (Peak - 1) * 1.5, GlassDock.RequiredLength(1, Size, Spacing, Peak), 9);
    }

    [Fact]
    public void 厚度预留放大空间()
    {
        var thickness = GlassDock.RequiredThickness(Size, Peak, 6);
        Assert.True(thickness >= Size * Peak);
        Assert.Equal(Size * Peak + 12, thickness, 9);
    }
}
