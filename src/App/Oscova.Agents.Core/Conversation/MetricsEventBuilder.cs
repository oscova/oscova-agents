using Oscova.Agents.Core.ApiContracts.Dashboard;

namespace Oscova.Agents.Core.Conversation;

/// <summary>
/// Builds metrics event payloads from the shared run ledgers.
/// </summary>
public static class MetricsEventBuilder
{
    /// <summary>Reserved agent key for usage from incognito conversations, kept apart from user agent names.</summary>
    public const string IncognitoBucket = "oscova:incognito";

    /// <summary>Reserved agent key for usage outside a conversation, such as titles and embeddings.</summary>
    public const string BackgroundBucket = "oscova:background";

    /// <summary>Retains token accounting under a bucket label without any conversation, task, or agent identity.</summary>
    public static MetricsEvent Anonymous(IReadOnlyList<AgentProviderUsage> usage, string bucketName) => new()
    {
        Usage = usage.Select(entry => entry with { AgentId = bucketName }).ToArray()
    };

    /// <summary>
    /// Sums per-provider usage entries into flat transcript totals.
    /// </summary>
    public static (long Input, long Output, long Total) SumUsage(IReadOnlyList<AgentProviderUsage> usage)
    {
        long input = 0;
        long output = 0;
        long total = 0;
        foreach (var entry in usage)
        {
            input += entry.InputTokens;
            output += entry.OutputTokens;
            total += entry.TotalTokens;
        }

        return (input, output, total);
    }

    /// <summary>
    /// Creates a metrics event from the current run context and ledger snapshots.
    /// </summary>
    public static MetricsEvent BuildEvent(
        RunContext context,
        string channel,
        IReadOnlyList<AgentProviderUsage> usage,
        IReadOnlyList<ToolInvocationRecord> tools,
        bool fallbackActivated) => new()
        {
            Channel = channel,
            ExecutionKind = context.ExecutionKind.ToString(),
            RunId = context.ExecutionId,
            TaskId = context.Task?.TaskId,
            Usage = usage,
            Tools = tools,
            FallbackActivated = fallbackActivated
        };
}
