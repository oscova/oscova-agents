using Oscova.Agents.Infrastructure.Plugins;
using Oscova.Agents.Llm;

namespace Oscova.Agents.Infrastructure.Chat;

/// <summary>
/// Merges built-in and plugin-contributed model providers for runtime resolution.
/// </summary>
public sealed class ModelProviderCatalog : IModelProviderCatalog
{

    internal ModelProviderCatalog(
        IEnumerable<IModelProvider> builtIns,
        PluginProviderActivation pluginProviders)
    {
        All = [.. builtIns, .. pluginProviders.ModelProviders];
    }

    public IReadOnlyList<IModelProvider> All { get; }

    public IModelProvider? Find(string type)
    {
        return All.FirstOrDefault(provider => string.Equals(provider.Type, type, StringComparison.Ordinal));
    }
}
