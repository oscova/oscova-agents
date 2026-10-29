using Oscova.Agents.Core.Plugins.Registry;

namespace Oscova.Agents.Core.ApiContracts.Plugins.Registry;

/// <summary>
/// Detailed remote plugin metadata returned for a single package.
/// </summary>
public sealed class PluginRegistryMetadataResponse
{
    public RemotePluginCatalogEntry? Entry { get; set; }
}
