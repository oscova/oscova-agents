using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Tasks;

/// <summary>
/// Holds contextual data for the currently executing agent task.
/// </summary>
public class AgentTaskContext
{
    /// <summary>
    /// Gets or sets the user that owns the running task.
    /// </summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the executing task identifier.
    /// </summary>
    public string TaskId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the agent identifier executing the task.
    /// </summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets serialized session state available to the task run.
    /// </summary>
    public JsonElement? SessionElement { get; set; }

    /// <summary>
    /// Gets or sets a callback that serializes the live session at handoff or persistence time.
    /// </summary>
    [JsonIgnore]
    public Func<CancellationToken, ValueTask<JsonElement?>>? SerializeLiveSessionAsync { get; set; }
}
