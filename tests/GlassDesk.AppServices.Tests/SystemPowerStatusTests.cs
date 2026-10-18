using GlassDesk.Platform.Power;

namespace GlassDesk.AppServices.Tests;

public sealed class SystemPowerStatusTests
{
    [Fact]
    public void ConvertsChargingBatteryAndEstimatedTime()
    {
        var status = SystemPowerStatus.FromNative(1, 8, 73, 1, 3600);

        Assert.True(status.IsAvailable);
        Assert.True(status.HasBattery);
        Assert.True(status.IsOnAcPower);
        Assert.Equal((byte)73, status.BatteryPercent);
        Assert.True(status.IsCharging);
        Assert.True(status.BatterySaverEnabled);
        Assert.Equal((uint)3600, status.EstimatedSecondsRemaining);
        Assert.Equal("正在充电 · 73%", status.ToIslandLabel());
    }

    [Fact]
    public void UnknownValuesStayUnknownAndDoNotInventBatteryData()
    {
        var unknown = SystemPowerStatus.FromNative(255, 255, 255, 0, uint.MaxValue);
        Assert.Null(unknown.IsOnAcPower);
        Assert.Null(unknown.HasBattery);
        Assert.Null(unknown.BatteryPercent);
        Assert.Null(unknown.EstimatedSecondsRemaining);
        Assert.False(unknown.IsCharging);
        Assert.Equal("电源状态未知", unknown.ToIslandLabel());

        var desktop = SystemPowerStatus.FromNative(1, 128, 255, 0, uint.MaxValue);
        Assert.False(desktop.HasBattery);
        Assert.Null(desktop.BatteryPercent);
        Assert.Null(desktop.EstimatedSecondsRemaining);
        Assert.Equal("已接通电源", desktop.ToIslandLabel());
    }

    [Fact]
    public void UnknownBatteryFlagDoesNotMasqueradeAsDesktopWithoutBattery()
    {
        var status = SystemPowerStatus.FromNative(1, 255, 255, 0, uint.MaxValue);

        Assert.Null(status.HasBattery);
        Assert.Null(status.BatteryPercent);
        Assert.Equal("接通电源 · 电池状态未知", status.ToIslandLabel());
    }

    [Fact]
    public void ClampsOutOfRangeBatteryPercentAndHandlesBatteryPower()
    {
        var status = SystemPowerStatus.FromNative(0, 0, 250, 0, uint.MaxValue);
        Assert.Equal((byte)100, status.BatteryPercent);
        Assert.False(status.IsOnAcPower);
        Assert.False(status.IsCharging);
    }
}
