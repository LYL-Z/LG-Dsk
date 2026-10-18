using GlassDesk.Platform.Displays;

namespace GlassDesk.Dock;

/// <summary>Conservatively removes an overlapping Windows taskbar from a monitor work area.</summary>
public static class TaskbarWorkAreaCalculator
{
    private const int EdgeTolerancePixels = 2;

    public static PixelRect ExcludeTaskbar(PixelRect displayBounds, PixelRect workArea, PixelRect? taskbarBounds)
    {
        if (taskbarBounds is not { Width: > 0, Height: > 0 } taskbar
            || !Intersects(workArea, taskbar)) return workArea;

        var touchesTop = Math.Abs(taskbar.Y - displayBounds.Y) <= EdgeTolerancePixels;
        var touchesBottom = Math.Abs(displayBounds.Bottom - taskbar.Bottom) <= EdgeTolerancePixels;
        var touchesLeft = Math.Abs(taskbar.X - displayBounds.X) <= EdgeTolerancePixels;
        var touchesRight = Math.Abs(displayBounds.Right - taskbar.Right) <= EdgeTolerancePixels;

        // A vertical taskbar commonly touches both the top and bottom edges, so a
        // nearest-edge tie must be resolved from its shape before choosing its side.
        if (taskbar.Height > taskbar.Width && (touchesLeft || touchesRight))
        {
            var edge = touchesLeft ? Edge.Left : Edge.Right;
            var verticalAdjusted = edge == Edge.Left ? FromLeft(workArea, taskbar.Right) : FromRight(workArea, taskbar.X);
            return verticalAdjusted.Width > 0 && verticalAdjusted.Height > 0 ? verticalAdjusted : workArea;
        }

        if (taskbar.Width >= taskbar.Height && (touchesTop || touchesBottom))
        {
            var edge = touchesTop && !touchesBottom ? Edge.Top : Edge.Bottom;
            var horizontalAdjusted = edge == Edge.Top ? FromTop(workArea, taskbar.Bottom) : FromBottom(workArea, taskbar.Y);
            return horizontalAdjusted.Width > 0 && horizontalAdjusted.Height > 0 ? horizontalAdjusted : workArea;
        }

        var edges = new[]
        {
            (Edge: Edge.Top, Distance: Math.Abs(taskbar.Y - displayBounds.Y)),
            (Edge: Edge.Bottom, Distance: Math.Abs(displayBounds.Bottom - taskbar.Bottom)),
            (Edge: Edge.Left, Distance: Math.Abs(taskbar.X - displayBounds.X)),
            (Edge: Edge.Right, Distance: Math.Abs(displayBounds.Right - taskbar.Right)),
        };
        var nearest = edges.OrderBy(candidate => candidate.Distance).First();
        if (nearest.Distance > EdgeTolerancePixels) return workArea;

        var adjusted = nearest.Edge switch
        {
            Edge.Top => FromTop(workArea, taskbar.Bottom),
            Edge.Bottom => FromBottom(workArea, taskbar.Y),
            Edge.Left => FromLeft(workArea, taskbar.Right),
            Edge.Right => FromRight(workArea, taskbar.X),
            _ => workArea,
        };
        return adjusted.Width > 0 && adjusted.Height > 0 ? adjusted : workArea;
    }

    private static PixelRect FromTop(PixelRect area, int bottom) =>
        new(area.X, Math.Max(area.Y, bottom), area.Width,
            Math.Max(0, area.Bottom - Math.Max(area.Y, bottom)));

    private static PixelRect FromBottom(PixelRect area, int top) =>
        new(area.X, area.Y, area.Width, Math.Max(0, Math.Min(area.Bottom, top) - area.Y));

    private static PixelRect FromLeft(PixelRect area, int right) =>
        new(Math.Max(area.X, right), area.Y, Math.Max(0, area.Right - Math.Max(area.X, right)), area.Height);

    private static PixelRect FromRight(PixelRect area, int left) =>
        new(area.X, area.Y, Math.Max(0, Math.Min(area.Right, left) - area.X), area.Height);

    private static bool Intersects(PixelRect first, PixelRect second) =>
        first.X < second.Right && first.Right > second.X && first.Y < second.Bottom && first.Bottom > second.Y;

    private enum Edge { Top, Bottom, Left, Right }
}
