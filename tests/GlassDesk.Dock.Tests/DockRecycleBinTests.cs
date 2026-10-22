using System.IO;
using GlassDesk.Platform.Shell;

namespace GlassDesk.Dock.Tests;

public sealed class DockRecycleBinTests
{
    [Fact]
    public void RecycleOperationRejectsAFileSystemRootBeforeCallingTheShell()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory);
        Assert.False(string.IsNullOrWhiteSpace(root));

        var outcome = RecycleBinShell.MoveToRecycleBin([root!]);

        Assert.Equal(RecycleBinOperationStatus.InvalidSource, outcome.Status);
        Assert.Equal(1, outcome.RequestedItemCount);
    }

    [Fact]
    public void RecycleOperationRejectsMissingPathsWithoutClaimingSuccess()
    {
        var missing = Path.Combine(Path.GetTempPath(), "LG-Dsk-missing-" + Guid.NewGuid().ToString("N"));

        var outcome = RecycleBinShell.MoveToRecycleBin([missing]);

        Assert.Equal(RecycleBinOperationStatus.InvalidSource, outcome.Status);
        Assert.Equal(1, outcome.RequestedItemCount);
    }
}
