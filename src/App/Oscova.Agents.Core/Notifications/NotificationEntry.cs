namespace Oscova.Agents.Core.Notifications;

/// <summary>
/// Stored notification shape for the per-user and system JSONL inbox files.
/// </summary>
public sealed record NotificationEntry(
    string Id,
    string Title,
    string Body,
    NotificationKind Kind,
    DateTimeOffset CreatedAt,
    bool IsRead,
    string? AgentId = null,
    string? TaskId = null,
    string? RunId = null,
    string? ThreadId = null,
    bool IsSystem = false,
    string? PluginId = null);
