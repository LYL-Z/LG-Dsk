namespace GlassDesk.Dock;

/// <summary>一枚磁贴在 Dock 轴向上的瞬时几何。</summary>
/// <param name="Center">磁贴中心沿 Dock 轴的位置（DIP，内容坐标系）。</param>
/// <param name="Scale">当前缩放。1 = 不放大。</param>
public readonly record struct TileGeometry(double Center, double Scale);

/// <summary>
/// 放大动画的几何计算——绘制与命中共用的唯一来源（D-05）。
/// </summary>
/// <remarks>
/// <para>
/// <b>连续排布。</b>缩放由指针到静止槽位的距离连续计算，再按每枚图标的实际宽度
/// 排布整行，邻居会自然让位。画布长度固定为峰值所需空间，放大时不扩窗也不裁切。
/// </para>
///
/// <para>
/// <b>视觉即所得。</b>命中判定使用与绘制<strong>同一份</strong>几何结果
/// （<see cref="Compute"/> 的输出同时喂绘制与 <see cref="HitTest"/>），
/// 重叠区域归<strong>缩放最大的磁贴</strong>——它绘制在最上层，看见谁点中谁。
/// 动画过程中两者也保持一致：几何是「当前指针位置」的纯函数，
/// 每一帧绘制与命中都用同一帧的计算结果。
/// </para>
///
/// <para>
/// 所有坐标沿 Dock 轴向（横排 Dock 是 X，竖排是 Y），单位 DIP。
/// 与垂直轴无关——垂直方向的放大膨胀由容器预留的厚度承担
/// （见 <see cref="RequiredThickness"/>）。
/// </para>
/// </remarks>
public static class DockMagnification
{
    /// <summary>Returns the smooth distance-to-scale curve shared across Dock entries.</summary>
    public static double ScaleAtDistance(double distance, double iconSize, double spacing, double peak)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(distance);
        ArgumentOutOfRangeException.ThrowIfLessThan(iconSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(spacing, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(peak, 1);

        return 1 + (peak - 1) * Falloff(distance, iconSize, spacing);
    }

    /// <summary>
    /// 计算每个磁贴的瞬时几何。返回的中心已含两端预留（见 <see cref="EndPadding"/>），
    /// 因此可直接当作画布坐标用于摆放与命中。
    /// </summary>
    /// <param name="count">磁贴数量。</param>
    /// <param name="iconSize">未放大图标边长，DIP。</param>
    /// <param name="spacing">图标间距，DIP。</param>
    /// <param name="peak">放大峰值倍数（D-17）。1 表示不放大。</param>
    /// <param name="pointerPosition">指针沿 Dock 轴的位置（画布坐标，DIP）；指针不在栏上时传 null。</param>
    public static TileGeometry[] Compute(
        int count,
        double iconSize,
        double spacing,
        double peak,
        double? pointerPosition)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var geometry = new TileGeometry[count];
        ComputeInto(geometry, iconSize, spacing, peak, pointerPosition);
        return geometry;
    }

    internal static void ComputeInto(Span<TileGeometry> geometry, double iconSize, double spacing,
        double peak, double? pointerPosition)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(iconSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(spacing, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(peak, 1);

        var extraWidth = 0d;
        for (var index = 0; index < geometry.Length; index++)
        {
            var center = SlotCenter(index, iconSize, spacing, peak);
            var scale = 1.0;

            if (pointerPosition is { } pointer)
            {
                var distance = Math.Abs(pointer - center);
                scale = 1 + (peak - 1) * Falloff(distance, iconSize, spacing);
            }

            geometry[index] = new TileGeometry(center, scale);
            extraWidth += iconSize * (scale - 1);
        }

        if (geometry.Length == 0) return;

        var baseLength = geometry.Length * iconSize + (geometry.Length - 1) * spacing;
        var availableLength = RequiredLength(geometry.Length, iconSize, spacing, peak);
        var rowLength = baseLength + extraWidth;
        var cursor = Math.Max(0, (availableLength - rowLength) / 2);
        for (var index = 0; index < geometry.Length; index++)
        {
            var width = iconSize * geometry[index].Scale;
            geometry[index] = new TileGeometry(cursor + width / 2, geometry[index].Scale);
            cursor += width + spacing;
        }

    }

    internal static int CompareDrawOrder(TileGeometry[] geometry, int left, int right)
    {
        var scaleOrder = geometry[right].Scale.CompareTo(geometry[left].Scale);
        return scaleOrder != 0 ? scaleOrder : left.CompareTo(right);
    }

    /// <summary>
    /// 绘制与命中<strong>共用</strong>的 z 序：缩放大的在上，同缩放时按索引。
    /// </summary>
    /// <remarks>
    /// 绘制侧拿它设 <c>Panel.ZIndex</c>，命中侧按同一顺序找第一个接住指针的磁贴。
    /// 两边共用这个函数，而不是各自写一遍排序——「看见谁就点中谁」才是结构上成立的，
    /// 而不是靠两处约定一致。
    /// </remarks>
    public static int[] TopToBottomOrder(TileGeometry[] geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        var order = Enumerable.Range(0, geometry.Length).ToArray();
        Array.Sort(order, Comparer<int>.Create((left, right) => CompareDrawOrder(geometry, left, right)));
        return order;
    }

    /// <summary>
    /// 命中测试：指针落在哪枚磁贴上（视觉即所得）。
    /// </summary>
    /// <remarks>
    /// 按 <see cref="TopToBottomOrder"/> 的顺序找第一个包含指针的磁贴。
    /// <paramref name="geometry"/> 必须是<strong>最后一次实际绘制</strong>的那一份
    /// （<c>DockTileStrip</c> 缓存绘制帧正是为此）——拿即时指针位置重算一份新的，
    /// 会在动画中途与屏幕上的画面分叉，出现「看见 A 点中 B」。
    /// 没有任何磁贴接住指针（落在间隙上）返回 -1。
    /// </remarks>
    public static int HitTest(TileGeometry[] geometry, double iconSize, double pointerPosition)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        foreach (var index in TopToBottomOrder(geometry))
        {
            var (center, scale) = geometry[index];
            var half = iconSize * scale / 2;

            if (pointerPosition >= center - half && pointerPosition < center + half)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>静止态下画布两端各留出的放大余量（DIP）。</summary>
    public static double EndPadding(double iconSize, double peak)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(iconSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(peak, 1);

        return (peak - 1) * iconSize * .75;
    }

    /// <summary>一行磁贴需要的总长度（沿 Dock 轴，DIP），含连续邻居波形的最坏放大余量。</summary>
    /// <remarks>
    /// 等于「未放大总长 + 两端预留」，为 1.5 个图标峰值余量预留空间，
    /// 与 <see cref="Compute"/> 的居中布局坐标对齐，避免邻居同时放大时裁切。
    /// </remarks>
    public static double RequiredLength(int count, double iconSize, double spacing, double peak)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        if (count == 0)
        {
            return 0;
        }

        return count * iconSize + (count - 1) * spacing + iconSize * (peak - 1) * 1.5;
    }

    /// <summary>
    /// 垂直于 Dock 轴需要的总厚度（DIP）：未放大厚度 + 放大膨胀。
    /// </summary>
    /// <remarks>
    /// 容器按这个厚度预留空间（修订 3：封顶时预留放大空间），
    /// 保证峰值放大时图标不被窗口裁切。
    /// </remarks>
    public static double RequiredThickness(double iconSize, double peak, double verticalPadding)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(iconSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(peak, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(verticalPadding, 0);

        return iconSize * peak + verticalPadding * 2;
    }

    /// <summary>
    /// 第 <paramref name="index"/> 枚磁贴的静止中心（<strong>画布坐标</strong>，DIP）。
    /// </summary>
    /// <remarks>
    /// 含两端预留（<see cref="EndPadding"/>）：先把整行向内让 `端部预留`，
    /// 首尾磁贴峰值放大后才不会越出画布。绘制与命中都以这个坐标为准，
    /// 因此调用方不需要（也不应该）自己再加偏移。
    /// </remarks>
    public static double SlotCenter(int index, double iconSize, double spacing, double peak)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        return EndPadding(iconSize, peak) + (index * (iconSize + spacing)) + (iconSize / 2);
    }

    /// <summary>
    /// 缩放衰减曲线：距离 0 处取 1（全额放大），1.5 个节距外平滑衰减到 0。
    /// </summary>
    /// <remarks>
    /// 用余弦钟形而不是线性三角：衰减平滑，动画不会在相邻磁贴之间出现折角。
    /// 1.5 个节距的影响半径让鼠标停在图标中心时邻居仍有轻微响应；
    /// 该范围内所有图标的最大总缩放余量不超过 1.5 枚图标的峰值余量。
    /// </remarks>
    private static double Falloff(double distance, double iconSize, double spacing)
    {
        var pitch = iconSize + spacing;
        var radius = pitch * 1.5;

        if (distance >= radius)
        {
            return 0;
        }

        var t = distance / radius;
        return 0.5 * (1 + Math.Cos(t * Math.PI));
    }

}
