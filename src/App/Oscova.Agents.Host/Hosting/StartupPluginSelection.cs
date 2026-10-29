using Oscova.Agents.Plugins;

namespace Oscova.Agents.Host.Hosting;

/// <summary>Describes an optional startup capability and its supported package editions.</summary>
public sealed record StartupPluginOption(string PluginId, string Name, string Description, bool SupportsCuda, string? Notice = null)
{
    public PluginHardwareRequirement? HardwareRequirement { get; init; }
    public PluginHardwareRequirement? CudaHardwareRequirement { get; init; }
}

/// <summary>Returns one completed package choice per selected startup capability.</summary>
public sealed record StartupPluginSelection(string PluginId, bool UseCuda = false)
{
    /// <summary>Rejects choices outside the offered capabilities before installation or decline persistence.</summary>
    public static void Validate(IReadOnlyList<StartupPluginOption> offered, IReadOnlyList<StartupPluginSelection> selected)
    {
        var options = offered.ToDictionary(item => item.PluginId, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in selected)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.PluginId) || !options.TryGetValue(item.PluginId, out var option))
            {
                throw new InvalidDataException("Selected plugin must be in the offered startup list.");
            }

            if (!seen.Add(item.PluginId)) { throw new InvalidDataException($"Select only one edition of plugin '{item.PluginId}'."); }

            if (item.UseCuda && !option.SupportsCuda) { throw new InvalidDataException($"Plugin '{item.PluginId}' does not offer NVIDIA CUDA on this platform."); }
        }
    }
}
