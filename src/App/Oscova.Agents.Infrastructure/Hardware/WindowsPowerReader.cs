using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Hardware;

namespace Oscova.Agents.Infrastructure.Hardware;

/// <summary>Translates the Windows system power reading into host facts.</summary>
internal static class WindowsPowerReader
{
    /// <summary>Reads the current native system result and reports a failed query.</summary>
    internal static HostPowerSnapshot Read(ILogger logger)
    {
        if (GetSystemPowerStatus(out var status)) { return Decode(status); }

        logger.LogWarning("GetSystemPowerStatus failed with error {ErrorCode}",
            Marshal.GetLastWin32Error());
        return new(null, null, null);
    }

    /// <summary>Preserves the independent unknown sentinels in the Windows result.</summary>
    internal static HostPowerSnapshot Decode(SystemPowerStatus status)
    {
        bool? hasBattery = status.BatteryFlag == 255
            ? null : (status.BatteryFlag & 128) == 0;
        double? percentage = hasBattery == false || status.BatteryLifePercent > 100
            ? null : status.BatteryLifePercent;
        bool? connected = status.ACLineStatus switch { 0 => false, 1 => true, _ => null };
        return new(hasBattery, percentage, connected);
    }

    /// <summary>Matches the fixed-width SYSTEM_POWER_STATUS native layout.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
