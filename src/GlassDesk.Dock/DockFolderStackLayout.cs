namespace GlassDesk.Dock;

internal readonly record struct DockFolderStackFanItem(double Left, double Top, double Rotation);

/// <summary>Pure placement math for the compact fan presentation used by folder stacks.</summary>
internal static class DockFolderStackLayout
{
    public const int MaximumFanItems = 7;
    public const double CanvasWidth = 320;
    public const double CanvasHeight = 130;
    public const double ItemWidth = 58;
    public const double ItemHeight = 76;

    public static IReadOnlyList<DockFolderStackFanItem> CalculateFan(int itemCount)
    {
        if (itemCount <= 0) return [];

        var visibleCount = Math.Min(itemCount, MaximumFanItems);
        var result = new DockFolderStackFanItem[visibleCount];
        const double radius = 118;
        const double pivotY = 168;
        const double maximumAngle = 42;

        for (var index = 0; index < visibleCount; index++)
        {
            var ratio = visibleCount == 1 ? .5 : index / (double)(visibleCount - 1);
            var angle = (ratio * 2 - 1) * maximumAngle;
            var radians = angle * Math.PI / 180;
            var centerX = CanvasWidth / 2 + radius * Math.Sin(radians);
            var top = pivotY - radius * Math.Cos(radians) - ItemHeight * .62;
            result[index] = new DockFolderStackFanItem(
                centerX - ItemWidth / 2,
                top,
                angle);
        }

        return result;
    }
}
