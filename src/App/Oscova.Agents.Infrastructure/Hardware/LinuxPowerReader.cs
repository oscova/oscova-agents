using System.Globalization;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Hardware;

namespace Oscova.Agents.Infrastructure.Hardware;

/// <summary>Reads OS-visible system batteries and external supplies from Linux sysfs.</summary>
internal static class LinuxPowerReader
{
    /// <summary>Reads a supply directory afresh and preserves incomplete inventory as unknown.</summary>
    internal static HostPowerSnapshot Read(string root, ILogger logger)
    {
        var batteries = new List<string>();
        var online = new List<bool?>();
        var incomplete = false;
        var unknownBatteryPresence = false;

        foreach (var supply in Directory.GetDirectories(root))
        {
            // The kernel excludes device-scoped supplies from system power reporting.
            if (Text(supply, "scope", logger) == "Device") { continue; }

            var type = Text(supply, "type", logger);
            if (type is null or "Unknown")
            {
                incomplete = true;
                continue;
            }

            if (type == "Battery")
            {
                // The kernel defines an omitted present property as a present battery.
                var present = Text(supply, "present", logger, missingValue: "1");
                if (present == "1") { batteries.Add(supply); }
                else if (present != "0") { unknownBatteryPresence = true; }
            }
            else if (type == "Mains" || type.StartsWith("USB", StringComparison.Ordinal)
                || type is "Wireless" or "BrickID")
            {
                online.Add(Number(supply, "online", logger) switch
                {
                    0 => false,
                    // Fixed and programmable online modes all establish external power.
                    1 or 2 or 3 => true,
                    _ => null
                });
            }
        }

        bool? hasBattery = batteries.Count > 0 ? true
            : incomplete || unknownBatteryPresence ? null : false;
        bool? connected = online.Contains(true) ? true
            : online.Count > 0 && !incomplete && online.All(value => value == false)
                ? false : null;
        double? percentage = null;

        if (!incomplete && !unknownBatteryPresence && batteries.Count == 1)
        {
            var battery = batteries[0];
            percentage = HostPowerService.Percentage(Number(battery, "energy_now", logger),
                Number(battery, "energy_full", logger))
                ?? HostPowerService.Percentage(Number(battery, "charge_now", logger),
                    Number(battery, "charge_full", logger))
                ?? HostPowerService.Percentage(Number(battery, "capacity", logger), 100);
        }
        else if (!incomplete && !unknownBatteryPresence && batteries.Count > 1)
        {
            double remaining = 0;
            double full = 0;
            foreach (var battery in batteries)
            {
                var energyNow = Number(battery, "energy_now", logger);
                var energyFull = Number(battery, "energy_full", logger);
                if (HostPowerService.Percentage(energyNow, energyFull) is null)
                {
                    return new(hasBattery, null, connected);
                }

                // Energy units are comparable across batteries, unlike charge or percent.
                remaining += energyNow!.Value;
                full += energyFull!.Value;
            }

            percentage = HostPowerService.Percentage(remaining, full);
        }

        return new(hasBattery, percentage, connected);
    }

    private static double? Number(string supply, string name, ILogger logger)
    {
        return double.TryParse(Text(supply, name, logger), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;
    }

    /// <summary>Distinguishes missing optional attributes from failed file access.</summary>
    private static string? Text(string supply, string name, ILogger logger, string? missingValue = null)
    {
        var path = Path.Combine(supply, name);
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch (FileNotFoundException)
        {
            return Directory.Exists(supply) ? missingValue : null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read power supply attribute {Path}", path);
            return null;
        }
    }
}
