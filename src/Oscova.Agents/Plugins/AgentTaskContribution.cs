using System;
using System.Collections.Generic;
using Oscova.Agents.Tasks;

namespace Oscova.Agents.Plugins;

/// <summary>Declares task defaults and optional code executed through the host task scheduler.</summary>
public sealed record AgentTaskContribution(
    string Key,
    Func<AgentPluginContext, AgentTaskDefinition> DefinitionFactory,
    Func<AgentPluginContext, IDeterministicTaskHandler>? DeterministicHandlerFactory = null,
    Func<AgentPluginContext, IAgentTaskHandler>? AgentHandlerFactory = null,
    Func<AgentPluginContext, AgentTaskDefinition, IReadOnlyList<Guid>>? RequiredProviders = null);
