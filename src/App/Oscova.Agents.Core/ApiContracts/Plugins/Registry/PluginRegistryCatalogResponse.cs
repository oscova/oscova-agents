using Oscova.Agents.Core.Plugins.Registry;

namespace Oscova.Agents.Core.ApiContracts.Plugins.Registry;

/// <summary>
/// Remote plugin catalog returned by the plugin registry API.
/// </summary>
public sealed class PluginRegistryCatalogResponse
{
    public IReadOnlyList<RemotePluginCatalogEntry> Entries { get; set; } = [];
}
