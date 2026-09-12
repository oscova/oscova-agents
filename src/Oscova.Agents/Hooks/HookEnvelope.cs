using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oscova.Agents.Hooks;

/// <summary>
/// JSON payload sent to command stdin and outbound HTTP hook handlers.
/// </summary>
public sealed class HookEnvelope
{
    /// <summary>
    /// Version of the external hook envelope schema.
    /// </summary>
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    /// Lifecycle event represented by this envelope.
    /// </summary>
    [JsonPropertyName("hook_event_name")]
    public HookEventKind HookEventName { get; init; }

    /// <summary>
    /// Unique identifier for this individual hook dispatch run.
    /// </summary>
    [JsonPropertyName("hook_run_id")]
    public string HookRunId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// UTC timestamp when the hook envelope was created.
    /// </summary>
    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Event-specific payload supplied by the hook consumer.
    /// </summary>
    [JsonPropertyName("event")]
    public required JsonElement Event { get; init; }

    /// <summary>
    /// Optional user identifier associated with the hook event.
    /// </summary>
    [JsonPropertyName("user_id")]
    public string? UserId { get; init; }

    /// <summary>
    /// Optional agent identifier associated with the hook event.
    /// </summary>
    [JsonPropertyName("agent_id")]
    public string? AgentId { get; init; }

    /// <summary>
    /// Optional chat thread identifier associated with the hook event.
    /// </summary>
    [JsonPropertyName("chat_thread_id")]
    public string? ChatThreadId { get; init; }

    /// <summary>
    /// Optional task identifier associated with the hook event.
    /// </summary>
    [JsonPropertyName("task_id")]
    public string? TaskId { get; init; }

    /// <summary>
    /// Optional task run identifier associated with the hook event.
    /// </summary>
    [JsonPropertyName("task_run_id")]
    public string? TaskRunId { get; init; }

    /// <summary>
    /// Optional execution kind associated with the hook event.
    /// </summary>
    [JsonPropertyName("execution_kind")]
    public string? ExecutionKind { get; init; }
}
