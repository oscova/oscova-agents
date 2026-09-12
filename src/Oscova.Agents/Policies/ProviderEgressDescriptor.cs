namespace Oscova.Agents.Policies;

/// <summary>
/// A by-value record of one agent run's resolved model provider and egress class.
/// </summary>
public sealed record ProviderEgressDescriptor(
    string AgentName,
    string ProviderName,
    EgressClass Egress,
    string? ProviderId = null,
    string? ModelId = null);
