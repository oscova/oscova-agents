using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Hardware;

namespace Oscova.Agents.Infrastructure.Hardware;

/// <summary>Reads host power directly from the current platform on each request.</summary>
public sealed class HostPowerService(ILogger<HostPowerService> logger) : IHostPowerService
{
    /// <summary>Reads a fresh host power snapshot from the supported operating system.</summary>
    public HostPowerSnapshot GetSnapshot()
    {
        try
        {
            if (OperatingSystem.IsWindows()) { return WindowsPowerReader.Read(logger); }

            if (OperatingSystem.IsMacOS()) { return MacPowerReader.Read(logger); }

            return LinuxPowerReader.Read("/sys/class/power_supply", logger);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or DllNotFoundException or EntryPointNotFoundException)
        {
            logger.LogWarning(ex, "Could not read host power state on {OperatingSystem}",
                Environment.OSVersion.Platform);
            return new(null, null, null);
        }
    }

    /// <summary>Converts compatible capacity readings without inventing or clamping values.</summary>
    internal static double? Percentage(double? current, double? full)
    {
        if (current is not { } remaining || full is not { } maximum
            || !double.IsFinite(remaining) || !double.IsFinite(maximum)
            || remaining < 0 || maximum <= 0 || remaining > maximum)
        {
            return null;
        }

        return remaining / maximum * 100;
    }
}
