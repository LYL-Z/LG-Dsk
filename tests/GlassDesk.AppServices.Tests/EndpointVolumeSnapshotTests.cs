using GlassDesk.Platform.Audio;

namespace GlassDesk.AppServices.Tests;

public sealed class EndpointVolumeSnapshotTests
{
    [Fact]
    public void FormatsVolumeMuteAndUnavailableWithoutClaimingUnsupportedData()
    {
        Assert.Equal("音量 · 42%", new EndpointVolumeSnapshot(true, 42, false, "endpoint").ToIslandLabel());
        Assert.Equal("静音 · —", new EndpointVolumeSnapshot(true, 0, true, "endpoint").ToIslandLabel());
        Assert.Equal("音量状态不可用", EndpointVolumeSnapshot.Unavailable.ToIslandLabel());
    }

    [Fact]
    public void ClampsDisplayedVolumeToTheSystemRange()
    {
        Assert.Equal("音量 · 100%", new EndpointVolumeSnapshot(true, 120, false, "endpoint").ToIslandLabel());
        Assert.Equal("音量 · 0%", new EndpointVolumeSnapshot(true, -5, false, "endpoint").ToIslandLabel());
    }
}
