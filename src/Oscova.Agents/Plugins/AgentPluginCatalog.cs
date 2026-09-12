using System.Collections.Generic;

namespace Oscova.Agents.Plugins;

/// <summary>
/// Holds the successfully loaded plugins for the lifetime of the host process.
/// </summary>
public sealed class AgentPluginCatalog : IAgentPluginCatalog
{
    /// <summary>
    /// Creates a catalog from loaded plugin entries and non-fatal diagnostics.
    /// </summary>
    /// <param name="plugins">The plugins that loaded successfully.</param>
    /// <param name="diagnostics">Diagnostics captured while scanning plugin manifests.</param>
    public AgentPluginCatalog(
        IReadOnlyList<LoadedAgentPlugin> plugins,
        IReadOnlyList<AgentPluginLoadDiagnostic> diagnostics)
    {
        Plugins = plugins;
        Diagnostics = diagnostics;
    }

    /// <inheritdoc />
    public IReadOnlyList<LoadedAgentPlugin> Plugins { get; }

    /// <inheritdoc />
    public IReadOnlyList<AgentPluginLoadDiagnostic> Diagnostics { get; }
}