using System.IO;
using GlassDesk.Core.Models.Settings;

namespace GlassDesk.Dock.Tests;

public sealed class DockFolderStackTests
{
    [Fact]
    public void FolderSnapshotScansAllNamesButKeepsOnlyTheGloballyFirstBoundedPage()
    {
        var visited = 0;
        IEnumerable<FolderStackCandidate> Source()
        {
            for (var index = 0; index < 20; index++)
            {
                visited++;
                var name = $"item-{19 - index:D2}.txt";
                yield return new FolderStackCandidate(
                    Path.Combine(@"C:\Downloads", name), name, false, DateTime.UnixEpoch);
            }
        }

        var result = FolderStackSnapshotPolicy.SelectItems(
            Source(), DockFolderStackSortMode.Name, maximumItems: 4);

        Assert.Equal(20, visited);
        Assert.True(result.HasMore);
        Assert.Equal(4, result.Items.Count);
        Assert.Equal(
            ["item-00.txt", "item-01.txt", "item-02.txt", "item-03.txt"],
            result.Items.Select(item => item.DisplayName).ToArray());
    }

    [Fact]
    public void FolderSnapshotExactLimitDoesNotClaimThereAreHiddenItems()
    {
        var result = FolderStackSnapshotPolicy.SelectItems(
            [Candidate("a.txt"), Candidate("b.txt")],
            DockFolderStackSortMode.Name,
            maximumItems: 2);

        Assert.False(result.HasMore);
        Assert.Equal(2, result.Items.Count);
    }

    [Fact]
    public void ModifiedDateSortPutsNewestFirstAndUsesNameAsStableTieBreaker()
    {
        var old = DateTime.UnixEpoch.AddDays(1);
        var recent = DateTime.UnixEpoch.AddDays(3);
        var result = FolderStackSnapshotPolicy.SelectItems(
            [Candidate("z-last.txt", recent), Candidate("b-same-time.txt", recent), Candidate("old.txt", old)],
            DockFolderStackSortMode.ModifiedDate);

        Assert.Equal(
            ["b-same-time.txt", "z-last.txt", "old.txt"],
            result.Items.Select(item => item.DisplayName).ToArray());
        Assert.False(result.HasMore);
    }

    [Fact]
    public void UnknownSortModeFallsBackToNameOrder()
    {
        var result = FolderStackSnapshotPolicy.SelectItems(
            [Candidate("z.txt"), Candidate("a.txt")],
            (DockFolderStackSortMode)999);

        Assert.Equal(["a.txt", "z.txt"], result.Items.Select(item => item.DisplayName).ToArray());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(7, 7)]
    [InlineData(50, 7)]
    public void FanLayoutBoundsEveryCardAndCapsTheFan(int itemCount, int expectedVisible)
    {
        var result = DockFolderStackLayout.CalculateFan(itemCount);

        Assert.Equal(expectedVisible, result.Count);
        foreach (var item in result)
        {
            Assert.True(double.IsFinite(item.Left));
            Assert.True(double.IsFinite(item.Top));
            Assert.InRange(item.Left, 0, DockFolderStackLayout.CanvasWidth - DockFolderStackLayout.ItemWidth);
            Assert.InRange(item.Top, 0, DockFolderStackLayout.CanvasHeight - DockFolderStackLayout.ItemHeight);
            Assert.InRange(item.Rotation, -42, 42);
        }
        Assert.Equal(result.OrderBy(item => item.Left).Select(item => item.Left), result.Select(item => item.Left));
    }

    private static FolderStackCandidate Candidate(string name, DateTime? lastModifiedUtc = null)
        => new(Path.Combine(@"C:\Downloads", name), name, false, lastModifiedUtc ?? DateTime.UnixEpoch);
}
