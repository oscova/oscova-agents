namespace Oscova.Agents.Core.Hardware;

/// <summary>Captures host capacity observations without persisting readiness or claiming free memory.</summary>
public sealed record HostHardwareSnapshot(DateTimeOffset ObservedAt, string Platform, string Architecture,
    int LogicalCpuCount, long? PhysicalMemoryBytes, long? ProcessMemoryBudgetBytes,
    IReadOnlyList<HostGraphicsAdapter> Adapters, bool DiscoveryAvailable, bool DevicePlacementUnambiguous = true);

/// <summary>Retains adapter identity and source so separate or unified memory is never silently combined.</summary>
public sealed record HostGraphicsAdapter(string Source, string Identity, string? Vendor, string Name,
    long? DedicatedMemoryBytes, bool? UnifiedMemory, string? DriverVersion = null);
