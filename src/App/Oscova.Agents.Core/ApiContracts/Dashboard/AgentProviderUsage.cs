namespace Oscova.Agents.Core.ApiContracts.Dashboard;

/// <summary>
/// Token usage attributed to a single agent, provider, and model within one run.
/// </summary>
public sealed record AgentProviderUsage(
    string AgentId,
    Guid ProviderId,
    string ModelId,
    decimal? InputCostPerMillionTokens,
    decimal? OutputCostPerMillionTokens,
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    bool HasMissingUsage = false);
