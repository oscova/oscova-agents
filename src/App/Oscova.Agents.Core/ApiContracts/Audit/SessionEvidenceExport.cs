using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Oscova.Agents.Core.ApiContracts.Audit;

/// <summary>
/// Versioned evidence payload exported from the session inspector for one chat thread or task run.
/// </summary>
public sealed class SessionEvidenceExport
{
    public const int CurrentSchemaVersion = 1;
    public const string ChatThreadScope = "chat-thread";
    public const string TaskRunScope = "task-run";

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("exportedAtUtc")]
    public DateTimeOffset ExportedAtUtc { get; init; }

    [JsonPropertyName("scope")]
    public string Scope { get; init; } = string.Empty;

    [JsonPropertyName("threadId")]
    public string? ThreadId { get; init; }

    [JsonPropertyName("taskId")]
    public string? TaskId { get; init; }

    [JsonPropertyName("runId")]
    public string? RunId { get; init; }

    [JsonPropertyName("messageCount")]
    public int MessageCount { get; init; }

    [JsonPropertyName("egressSummary")]
    public EgressAuditSummary EgressSummary { get; init; } = new(0, 0, 0, 0);

    [JsonPropertyName("systemInstruction")]
    public string? SystemInstruction { get; init; }

    [JsonPropertyName("messages")]
    public IReadOnlyList<ChatMessage> Messages { get; init; } = [];

    [JsonPropertyName("egressEntries")]
    public IReadOnlyList<EgressAuditEntry> EgressEntries { get; init; } = [];

    /// <summary>
    /// Builds an export envelope for a persisted chat thread.
    /// </summary>
    public static SessionEvidenceExport ForChatThread(
        string threadId,
        string? systemInstruction,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<EgressAuditEntry> egressEntries)
    {
        return new SessionEvidenceExport
        {
            ExportedAtUtc = DateTimeOffset.UtcNow,
            Scope = ChatThreadScope,
            ThreadId = threadId,
            MessageCount = messages.Count,
            EgressSummary = BuildSummary(egressEntries),
            SystemInstruction = systemInstruction,
            Messages = [.. messages],
            EgressEntries = [.. egressEntries]
        };
    }

    /// <summary>
    /// Builds an export envelope for a persisted or live task run.
    /// </summary>
    public static SessionEvidenceExport ForTaskRun(
        string taskId,
        string runId,
        string? systemInstruction,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<EgressAuditEntry> egressEntries)
    {
        return new SessionEvidenceExport
        {
            ExportedAtUtc = DateTimeOffset.UtcNow,
            Scope = TaskRunScope,
            TaskId = taskId,
            RunId = runId,
            MessageCount = messages.Count,
            EgressSummary = BuildSummary(egressEntries),
            SystemInstruction = systemInstruction,
            Messages = [.. messages],
            EgressEntries = [.. egressEntries]
        };
    }

    /// <summary>
    /// Counts egress rows by the filterable audit kinds.
    /// </summary>
    public static EgressAuditSummary BuildSummary(IReadOnlyList<EgressAuditEntry> entries)
    {
        var providerCount = entries.Count(entry => string.Equals(entry.Record.Kind, EgressAuditKinds.Provider, StringComparison.OrdinalIgnoreCase));
        var toolCount = entries.Count(entry => string.Equals(entry.Record.Kind, EgressAuditKinds.Tool, StringComparison.OrdinalIgnoreCase));
        var embeddingCount = entries.Count(entry => string.Equals(entry.Record.Kind, EgressAuditKinds.Embedding, StringComparison.OrdinalIgnoreCase));

        return new EgressAuditSummary(entries.Count, providerCount, toolCount, embeddingCount);
    }
}
