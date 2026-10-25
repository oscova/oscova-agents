namespace Oscova.Agents.Policies;

/// <summary>
/// A by-value record of one agent run's resolved model provider and egress class.
/// </summary>
/// <remarks>Holds ids only, because egress records are plaintext metadata and display names are user content.</remarks>
public sealed record ProviderEgressDescriptor(
    string AgentId,
    EgressClass Egress,
    string ProviderId,
    string? ModelId = null);
