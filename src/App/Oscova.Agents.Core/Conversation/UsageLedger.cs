using Oscova.Agents.Core.ApiContracts.Dashboard;

namespace Oscova.Agents.Core.Conversation;

/// <summary>
/// Accumulates model token usage for a single run, keyed by agent, provider, and model.
/// </summary>
public sealed class UsageLedger
{
    /// <summary>Reserved agent key for usage recorded outside any agent run.</summary>
    public const string PrimaryBucket = "(primary)";

    private readonly Lock _gate = new();
    private readonly Dictionary<(string Agent, Guid ProviderId, string ModelId), UsageAccumulator> _entries = [];

    /// <summary>
    /// Adds one response's usage to the running totals.
    /// </summary>
    public void Add(
        string agentId,
        Guid providerId,
        string modelId,
        decimal? inputCostPerMillionTokens,
        decimal? outputCostPerMillionTokens,
        long? inputTokens,
        long? outputTokens,
        long? totalTokens)
    {
        var normalizedAgentId = string.IsNullOrWhiteSpace(agentId) ? PrimaryBucket : agentId;
        var normalizedModelId = string.IsNullOrWhiteSpace(modelId) ? "unknown" : modelId;
        var key = (normalizedAgentId, providerId, normalizedModelId);

        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var accumulator))
            {
                accumulator = new UsageAccumulator(inputCostPerMillionTokens, outputCostPerMillionTokens);
                _entries[key] = accumulator;
            }

            accumulator.HasMissingUsage |= inputTokens is null && outputTokens is null;
            accumulator.InputTokens += inputTokens ?? 0;
            accumulator.OutputTokens += outputTokens ?? 0;
            accumulator.TotalTokens += totalTokens ?? ((inputTokens ?? 0) + (outputTokens ?? 0));
        }
    }

    /// <summary>
    /// Returns the per-agent, provider, and model usage entries.
    /// </summary>
    public IReadOnlyList<AgentProviderUsage> Snapshot()
    {
        lock (_gate)
        {
            var result = new List<AgentProviderUsage>(_entries.Count);
            foreach (var entry in _entries)
            {
                result.Add(new AgentProviderUsage(
                    entry.Key.Agent,
                    entry.Key.ProviderId,
                    entry.Key.ModelId,
                    entry.Value.InputCostPerMillionTokens,
                    entry.Value.OutputCostPerMillionTokens,
                    entry.Value.InputTokens,
                    entry.Value.OutputTokens,
                    entry.Value.TotalTokens,
                    entry.Value.HasMissingUsage));
            }

            return result;
        }
    }

    private sealed class UsageAccumulator(decimal? inputCostPerMillionTokens, decimal? outputCostPerMillionTokens)
    {
        public decimal? InputCostPerMillionTokens { get; } = inputCostPerMillionTokens;
        public decimal? OutputCostPerMillionTokens { get; } = outputCostPerMillionTokens;
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long TotalTokens { get; set; }
        public bool HasMissingUsage { get; set; }
    }
}
