namespace Oscova.Agents.Core.ApiContracts.Dashboard;

/// <summary>
/// One append-only usage record written at the end of an agent run or chat turn.
/// </summary>
public sealed record MetricsEvent
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Messaging surface such as web_chat, agent_task, telegram, or discord.
    /// </summary>
    public string Channel { get; init; } = string.Empty;

    /// <summary>
    /// Run kind from RunExecutionKind.
    /// </summary>
    public string ExecutionKind { get; init; } = string.Empty;

    /// <summary>
    /// Stable execution id for this run.
    /// </summary>
    public string RunId { get; init; } = string.Empty;

    /// <summary>
    /// Owning task definition id when this run is a task.
    /// </summary>
    public string? TaskId { get; init; }

    /// <summary>
    /// How a task run was triggered.
    /// </summary>
    public string? TriggerSource { get; init; }

    /// <summary>
    /// Final task status for agent_task runs.
    /// </summary>
    public string? TaskStatus { get; init; }

    /// <summary>
    /// Wall-clock run duration in seconds when known.
    /// </summary>
    public double? DurationSeconds { get; init; }

    /// <summary>
    /// Per agent, provider, and model token usage for this run.
    /// </summary>
    public IReadOnlyList<AgentProviderUsage> Usage { get; init; } = [];

    /// <summary>
    /// Tool invocations recorded for this run.
    /// </summary>
    public IReadOnlyList<ToolInvocationRecord> Tools { get; init; } = [];

    /// <summary>
    /// True when the fallback provider served any response in this run.
    /// </summary>
    public bool FallbackActivated { get; init; }
}
