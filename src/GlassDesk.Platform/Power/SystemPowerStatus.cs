using System.Runtime.InteropServices;

namespace GlassDesk.Platform.Power;

/// <summary>Normalized system-wide power information. Unknown values remain unknown.</summary>
public readonly record struct SystemPowerStatus(
    bool IsAvailable,
    bool? HasBattery,
    bool? IsOnAcPower,
    byte? BatteryPercent,
    bool IsCharging,
    bool BatterySaverEnabled,
    uint? EstimatedSecondsRemaining)
{
    public string ToIslandLabel()
    {
        if (!IsAvailable) return "电源状态不可用";
        if (HasBattery == false) return IsOnAcPower == true ? "已接通电源" : "无电池";
        if (HasBattery is null) return IsOnAcPower == true ? "接通电源 · 电池状态未知" : "电源状态未知";

        var percent = BatteryPercent is { } value ? $"{value}%" : "电量未知";
        var source = IsOnAcPower switch { true => "接通电源", false => "电池供电", _ => "电源未知" };
        return IsCharging ? $"正在充电 · {percent}" : $"{source} · {percent}";
    }

    public static SystemPowerStatus FromNative(byte acLineStatus, byte batteryFlag, byte batteryLifePercent,
        byte systemStatusFlag, uint batteryLifeTime)
    {
        bool? hasBattery = batteryFlag switch { 128 => false, 255 => null, _ => true };
        var percent = batteryLifePercent == 255 ? null : (byte?)Math.Min(batteryLifePercent, (byte)100);
        var ac = acLineStatus switch { 0 => false, 1 => true, _ => (bool?)null };
        var life = batteryLifeTime == uint.MaxValue ? null : (uint?)batteryLifeTime;
        return new(true, hasBattery, ac, hasBattery == true ? percent : null, hasBattery == true && (batteryFlag & 0x08) != 0,
            (systemStatusFlag & 0x01) != 0, hasBattery == true ? life : null);
    }
}

/// <summary>Reads the public Win32 aggregate power status; never invents battery data.</summary>
public static partial class SystemPowerStatusReader
{
    public static SystemPowerStatus Read()
    {
        if (!OperatingSystem.IsWindows()) return default;
        try
        {
            if (!GetSystemPowerStatus(out var status)) return default;
            return SystemPowerStatus.FromNative(status.ACLineStatus, status.BatteryFlag,
                status.BatteryLifePercent, status.SystemStatusFlag, status.BatteryLifeTime);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return default;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out NativePowerStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }
}
