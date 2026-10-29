using System.Text.Json;
using Oscova.Agents.Plugins;

namespace Oscova.Agents.Infrastructure.Plugins.Registry;

/// <summary>Records the NuGet package identity carried by an installed plugin directory.</summary>
public sealed record InstalledPluginPackage(string PackageId, string Version)
{
    /// <summary>Gets whether the operator deliberately chose this release instead of a reviewed default.</summary>
    public bool ExplicitSelection { get; init; }
    public const string FileName = "oscova-package.json";

    /// <summary>Reads the package identity beside an inspected plugin manifest when present.</summary>
    public static InstalledPluginPackage? Read(AgentPluginInspection? inspection)
    {
        if (inspection is null) { return null; }

        var path = Path.Combine(Path.GetDirectoryName(inspection.ManifestPath)!, FileName);
        if (!File.Exists(path)) { return null; }

        return JsonSerializer.Deserialize<InstalledPluginPackage>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Plugin package identity is empty.");
    }

    /// <summary>Writes the package identity into a validated installation directory.</summary>
    public Task WriteAsync(string directory, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(Path.Combine(directory, FileName),
            JsonSerializer.Serialize(this), cancellationToken);
}
