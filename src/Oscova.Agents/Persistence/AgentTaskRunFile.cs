using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Oscova.Agents.Persistence;

/// <summary>
/// One bounded row in tasks/{slug}/runs/index.jsonl.
/// </summary>
public sealed class AgentTaskRunIndexFile
{
    /// <summary>Gets or sets the file schema version.</summary>
    [JsonPropertyName("v")]
    public int Version { get; set; } = 1;

    /// <summary>Gets or sets the run identifier.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the owning task identifier.</summary>
    [JsonPropertyName("task_id")]
    public string TaskId { get; set; } = string.Empty;

    /// <summary>Gets or sets the serialized run status.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets the ISO timestamp when the run started.</summary>
    [JsonPropertyName("started_at")]
    public string? StartedAt { get; set; }

    /// <summary>Gets or sets the ISO timestamp when the run completed.</summary>
    [JsonPropertyName("completed_at")]
    public string? CompletedAt { get; set; }

    /// <summary>Gets or sets the persisted result summary.</summary>
    [JsonPropertyName("result_summary")]
    public string? ResultSummary { get; set; }

    /// <summary>Gets or sets the failure message when the run failed.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>Gets or sets the token usage recorded for the run.</summary>
    [JsonPropertyName("tokens_used")]
    public int? TokensUsed { get; set; }

    /// <summary>Gets or sets how the run was triggered.</summary>
    [JsonPropertyName("trigger_source")]
    public string? TriggerSource { get; set; }

    /// <summary>Gets or sets validated trigger parameters for the run.</summary>
    [JsonPropertyName("trigger_parameters")]
    public IDictionary<string, JsonElement>? TriggerParameters { get; set; }


    /// <summary>Gets or sets the first captured system instruction for the run.</summary>
    [JsonPropertyName("system_instruction")]
    public string? SystemInstruction { get; set; }


}

/// <summary>
/// One versioned row in a task run raw-message transcript file.
/// </summary>
public sealed class AgentTaskRunTranscriptFile
{
    /// <summary>Gets or sets the file schema version.</summary>
    [JsonPropertyName("v")]
    public int Version { get; set; } = 1;

    /// <summary>Gets or sets whether the row is a header or message.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "message";

    /// <summary>Gets or sets the run identifier.</summary>
    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = string.Empty;

    /// <summary>Gets or sets the owning task identifier.</summary>
    [JsonPropertyName("task_id")]
    public string TaskId { get; set; } = string.Empty;

    /// <summary>Gets or sets the header's captured system instruction.</summary>
    [JsonPropertyName("system_instruction")]
    public string? SystemInstruction { get; set; }

    /// <summary>Gets or sets the raw model message for message rows.</summary>
    [JsonPropertyName("message")]
    public ChatMessage? Message { get; set; }


}
