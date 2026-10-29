using System.Text.Json.Serialization;

namespace Oscova.Agents.Core.ApiContracts.Audit;

/// <summary>
/// Describes a bounded egress audit browse request from the UI.
/// </summary>
public sealed record EgressAuditQueryRequest
{
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 50;

    public DateTimeOffset? FromUtc { get; init; }

    public DateTimeOffset? ToUtc { get; init; }

    public string? Kind { get; init; }

    public string? Source { get; init; }

    public string? SearchTerm { get; init; }

    /// <summary>Agent, provider and MCP server ids whose labels match <see cref="SearchTerm"/>, resolved by the host.</summary>
    [JsonIgnore]
    public IReadOnlyCollection<string> SearchIds { get; init; } = [];
}

/// <summary>
/// One UI-ready egress audit row with optional owner information for admin review.
/// </summary>
public sealed record EgressAuditEntry
{
    public required EgressAuditRecord Record { get; init; }

    public string? OwnerUserId { get; init; }

    public string? OwnerDisplayName { get; init; }

    public string? AgentLabel { get; init; }

    public string? ProviderLabel { get; init; }

    public string? ToolLabel { get; init; }
}

/// <summary>
/// One bounded egress audit page plus simple counts for the active filter.
/// </summary>
public sealed record EgressAuditPage
{
    public required IReadOnlyList<EgressAuditEntry> Entries { get; init; }

    public required int CurrentPage { get; init; }

    public required int PageSize { get; init; }

    public required int TotalCount { get; init; }

    public required int TotalPages { get; init; }

    public required EgressAuditSummary Summary { get; init; }
}

/// <summary>
/// Counts the active egress audit result by the event kinds users can filter on.
/// </summary>
public sealed record EgressAuditSummary(
    int Total,
    int ProviderCount,
    int ToolCount,
    int EmbeddingCount);
