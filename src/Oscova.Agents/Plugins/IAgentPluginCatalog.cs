using System.Collections.Generic;

namespace Oscova.Agents.Plugins;

/// <summary>
/// Exposes the plugins loaded for the current host process.
/// </summary>
public interface IAgentPluginCatalog
{
    /// <summary>
    /// Successfully loaded plugins and their declared contributions.
    /// </summary>
    IReadOnlyList<LoadedAgentPlugin> Plugins { get; }

    /// <summary>
    /// Non-fatal load issues the host should log or surface.
    /// </summary>
    IReadOnlyList<AgentPluginLoadDiagnostic> Diagnostics { get; }
}