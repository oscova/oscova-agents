using System;

namespace Oscova.Agents.Plugins;

/// <summary>
/// Describes a toolset a loaded plugin can create for a user scope.
/// </summary>
public sealed record AgentToolsetContribution(
    Type ToolsetType,
    Func<AgentPluginContext, ICrewToolset> Factory);