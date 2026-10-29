namespace Oscova.Agents.Core.ApiContracts.Audit;

/// <summary>
/// One append-only egress audit line for a single time data left the machine.
/// </summary>
/// <remarks>The file is plaintext metadata, so it holds ids only; readers resolve labels after unlock.</remarks>
public sealed record EgressAuditRecord
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public string Kind { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public string? ThreadId { get; init; }

    public string? RunId { get; init; }

    public string? TaskId { get; init; }

    public string? AgentId { get; init; }

    public string? ProviderId { get; init; }

    public string? ModelId { get; init; }

    /// <summary>The plain tool name; an MCP tool also sets <see cref="McpServerId"/>.</summary>
    public string? ToolName { get; init; }

    public string? McpServerId { get; init; }
}

/// <summary>
/// Known values for egress audit kind and source fields.
/// </summary>
public static class EgressAuditKinds
{
    public const string Provider = "provider";
    public const string Tool = "tool";
    public const string Embedding = "embedding";

    public const string SourceChat = "chat";
    public const string SourceTask = "task";
    public const string SourceEmbeddingIndexing = "embedding-indexing";
    public const string SourceEmbeddingSearch = "embedding-search";
}
