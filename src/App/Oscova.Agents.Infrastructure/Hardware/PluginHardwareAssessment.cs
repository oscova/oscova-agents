using Oscova.Agents.Plugins;
using Oscova.Agents.Core.Hardware;

namespace Oscova.Agents.Infrastructure.Hardware;

/// <summary>Separates a package's requirements from passive observations and untested execution.</summary>
public sealed record PluginHardwareAssessment(PluginHardwareRequirement? Requirement,
    IReadOnlyList<HostGraphicsAdapter> MatchingAdapters, string Observation)
{
    /// <summary>Assesses declarations without loading the plugin or claiming runtime readiness.</summary>
    public static PluginHardwareAssessment Assess(PluginHardwareRequirement? requirement, HostHardwareSnapshot? hardware)
    {
        if (requirement?.IsKnown != true) { return new(null, [], "unknown"); }

        if (hardware?.DiscoveryAvailable != true) { return new(requirement, [], "unknown"); }

        if (requirement.Backend == "managed") { return new(requirement, [], "managed"); }

        if (requirement.Backend == "cpu") { return new(requirement, [], "observed"); }

        var adapters = hardware.Adapters.Where(adapter => requirement.Backend == "cuda"
            ? adapter.Vendor == "NVIDIA" : adapter.Vendor == "Apple" && adapter.UnifiedMemory == true).ToArray();
        return new(requirement, adapters, adapters.Length == 0 ? "not-detected" : "observed");
    }
}
