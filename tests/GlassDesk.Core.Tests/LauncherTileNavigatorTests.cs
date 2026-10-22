using GlassDesk.Core.Models;

namespace GlassDesk.Core.Tests;

public sealed class LauncherTileNavigatorTests
{
    [Theory]
    [InlineData(0, LauncherTileDirection.Right, 1)]
    [InlineData(1, LauncherTileDirection.Down, 5)]
    [InlineData(5, LauncherTileDirection.Up, 1)]
    [InlineData(7, LauncherTileDirection.Left, 6)]
    public void MovesToAdjacentTileOnVisibleGrid(int current, LauncherTileDirection direction, int expected)
    {
        Assert.True(LauncherTileNavigator.TryMove(current, itemCount: 9, columns: 4, direction, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0, LauncherTileDirection.Left)]
    [InlineData(3, LauncherTileDirection.Right)]
    [InlineData(1, LauncherTileDirection.Up)]
    [InlineData(5, LauncherTileDirection.Down)]
    public void DoesNotWrapAcrossRowsOrPastVisibleItems(int current, LauncherTileDirection direction)
    {
        Assert.False(LauncherTileNavigator.TryMove(current, itemCount: 6, columns: 4, direction, out var actual));
        Assert.Equal(current, actual);
    }

    [Theory]
    [InlineData(-1, 4, 2)]
    [InlineData(4, 4, 2)]
    [InlineData(0, 0, 2)]
    [InlineData(0, 4, 0)]
    public void RejectsInvalidGridCoordinates(int current, int count, int columns)
    {
        Assert.False(LauncherTileNavigator.TryMove(current, count, columns, LauncherTileDirection.Right, out var actual));
        Assert.Equal(current, actual);
    }
}
