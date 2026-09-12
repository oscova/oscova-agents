using System.Collections.Generic;

namespace Oscova.Agents.Llm;

/// <summary>
/// Provides the merged set of built-in and plugin-contributed model providers.
/// </summary>
public interface IModelProviderCatalog
{
    /// <summary>
    /// Gets every available model provider.
    /// </summary>
    IReadOnlyList<IModelProvider> All { get; }

    /// <summary>
    /// Finds a provider by its open string key.
    /// </summary>
    IModelProvider? Find(string type);
}
