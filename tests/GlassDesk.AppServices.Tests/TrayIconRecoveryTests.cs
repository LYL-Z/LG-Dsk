using GlassDesk.Platform;

namespace GlassDesk.AppServices.Tests;

public sealed class TrayIconRecoveryTests
{
    [Fact]
    public void ExplorerRestartNotificationUsesRegisteredWindowMessageRange()
    {
        Assert.InRange(TrayIcon.TaskbarCreatedMessage, 0xC000, 0xFFFF);
    }
}
