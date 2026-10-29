namespace Oscova.Agents.Core.Hardware;

/// <summary>Supplies current host power facts for consumers making their own decisions.</summary>
public interface IHostPowerService
{
    /// <summary>Reads the current host power state, preserving unavailable fields as null.</summary>
    HostPowerSnapshot GetSnapshot();
}

/// <summary>Describes OS-visible host power state, with null representing an unknown field.</summary>
public sealed record HostPowerSnapshot(bool? HasBattery, double? BatteryPercentage, bool? IsExternalPowerConnected)
{
    /// <summary>Confirms that battery protection can be configured from the current host facts.</summary>
    public bool HasUsableBatteryInformation =>
        HasBattery is true && BatteryPercentage is not null && IsExternalPowerConnected is not null;
}
