using System.Collections.Generic;
using System.Threading;

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
        _diagnostics = [.. diagnostics];
    }

    private readonly Lock _diagnosticGate = new();
    private readonly List<AgentPluginLoadDiagnostic> _diagnostics;

    /// <inheritdoc />
    public IReadOnlyList<LoadedAgentPlugin> Plugins { get; }

    /// <inheritdoc />
    public IReadOnlyList<AgentPluginLoadDiagnostic> Diagnostics
    {
        get { lock (_diagnosticGate) { return _diagnostics.ToArray(); } }
    }

    /// <inheritdoc />
    public void ReportDiagnostic(AgentPluginLoadDiagnostic diagnostic)
    {
        lock (_diagnosticGate)
        {
            if (!_diagnostics.Contains(diagnostic)) { _diagnostics.Add(diagnostic); }
        }
    }
}
