namespace GlassDesk.Core.Models;

public enum LauncherTileDirection
{
    Left,
    Right,
    Up,
    Down,
}

/// <summary>Pure row/column navigation for the currently visible Launcher page.</summary>
public static class LauncherTileNavigator
{
    public static bool TryMove(int currentIndex, int itemCount, int columns,
        LauncherTileDirection direction, out int targetIndex)
    {
        targetIndex = currentIndex;
        if (itemCount <= 0 || columns <= 0 || currentIndex < 0 || currentIndex >= itemCount)
            return false;

        var row = currentIndex / columns;
        var column = currentIndex % columns;
        var candidate = direction switch
        {
            LauncherTileDirection.Left when column > 0 => currentIndex - 1,
            LauncherTileDirection.Right when currentIndex + 1 < itemCount
                && (currentIndex + 1) / columns == row => currentIndex + 1,
            LauncherTileDirection.Up when row > 0 => currentIndex - columns,
            LauncherTileDirection.Down when currentIndex + columns < itemCount => currentIndex + columns,
            _ => -1,
        };

        if (candidate < 0) return false;
        targetIndex = candidate;
        return true;
    }
}
