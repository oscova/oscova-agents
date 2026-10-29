using System.Text.Json.Serialization;

namespace Oscova.Agents.Core.ApiContracts.Dashboard;

/// <summary>
/// Usage and operational statistics rendered by the metrics dashboard.
/// </summary>
public sealed record DashboardSnapshot
{
    public int AgentCount { get; init; }
    public int ModelProviderCount { get; init; }
    public int TaskCount { get; init; }
    public int ToolCount { get; init; }
    public int McpServerCount { get; init; }
    public int SkillCount { get; init; }
    public int ThreadCount { get; init; }

    public long TotalInputTokens { get; init; }
    public long TotalOutputTokens { get; init; }
    public long TotalTokens { get; init; }

    public decimal EstimatedCost { get; init; }
    public bool HasUnpricedUsage { get; init; }
    public IReadOnlyList<CostBreakdownEntry> CostByProvider { get; init; } = [];

    public IReadOnlyList<TokenBreakdownEntry> TokensByProvider { get; init; } = [];
    public IReadOnlyList<TokenBreakdownEntry> TokensByAgent { get; init; } = [];
    public IReadOnlyList<TokenBreakdownEntry> TokensByModel { get; init; } = [];

    public int EgressCount { get; init; }
    public IReadOnlyList<EgressBreakdownEntry> EgressByProvider { get; init; } = [];
    public IReadOnlyList<EgressBreakdownEntry> EgressByAgent { get; init; } = [];

    public int ToolInvocationCount { get; init; }
    public IReadOnlyList<ToolUsageEntry> ToolUsage { get; init; } = [];

    public int TaskRunCount { get; init; }
    public int TaskSucceededCount { get; init; }
    public int TaskFailedCount { get; init; }
    public double TaskSuccessRate { get; init; }
    public double AverageTaskDurationSeconds { get; init; }
    public IReadOnlyList<CountByLabel> TaskRunsByTrigger { get; init; } = [];

    public int FallbackActivationCount { get; init; }

    public IReadOnlyList<UsageTimePoint> TokenTimeline { get; init; } = [];
    public IReadOnlyList<UsageTimePoint> EgressTimeline { get; init; } = [];

    public bool IsAdminView { get; init; }

    /// <summary>
    /// This user's breakdowns as the operator may see them, used only for the system-wide view and never sent to clients.
    /// </summary>
    [JsonIgnore]
    public DashboardSnapshot? SystemContribution { get; init; }
    public int? UserCount { get; init; }
    public IReadOnlyList<PerUserUsageEntry>? UsageByUser { get; init; }
    public DashboardSnapshot? SystemWideUsage { get; init; }
}

/// <summary>
/// Token totals grouped by provider, agent, or model. <c>Key</c> holds the id or reserved bucket key, so equally named rows stay apart.
/// </summary>
public sealed record TokenBreakdownEntry(string Name, long InputTokens, long OutputTokens, long TotalTokens, string? Key = null);

/// <summary>
/// Estimated usage cost grouped by provider. <c>Key</c> holds the id or reserved bucket key, so equally named rows stay apart.
/// </summary>
public sealed record CostBreakdownEntry(string Name, long TotalTokens, decimal EstimatedCost, string? Key = null);

/// <summary>
/// Egress count grouped by display label. <c>Key</c> holds the id or reserved bucket key, so equally named rows stay apart.
/// </summary>
public sealed record EgressBreakdownEntry(string Name, int Count, string? Key = null);

/// <summary>
/// Tool invocation success and failure counts grouped by tool name.
/// </summary>
public sealed record ToolUsageEntry(string ToolName, int Succeeded, int Failed);

/// <summary>
/// Generic count grouped by a display label.
/// </summary>
public sealed record CountByLabel(string Label, int Count);

/// <summary>
/// Daily usage point for timeline charts.
/// </summary>
public sealed record UsageTimePoint(
    DateOnly Date,
    long Tokens,
    int Egress,
    long InputTokens = 0,
    long OutputTokens = 0);

/// <summary>
/// Per-user usage and storage summary shown only to admins.
/// </summary>
public sealed record PerUserUsageEntry(
    string UserName,
    long TotalTokens,
    int EgressCount,
    long UserStorageBytes = 0,
    int UserFileCount = 0);
