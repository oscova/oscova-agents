using System;

namespace Oscova.Agents.Plugins;

/// <summary>Describes a provider a loaded plugin can create through its plugin context.</summary>
public sealed record AgentProviderContribution<TProvider>(
    Type ProviderType,
    Func<AgentPluginContext, TProvider> Factory)
    where TProvider : class;
