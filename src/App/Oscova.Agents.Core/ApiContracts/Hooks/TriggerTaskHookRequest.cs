using System.Text.Json;
using Oscova.Agents.Tasks;

namespace Oscova.Agents.Core.ApiContracts.Hooks;

/// <summary>
/// Request to trigger a task through the inbound hook API.
/// </summary>
public sealed class TriggerTaskHookRequest
{
    public IDictionary<string, JsonElement> Parameters { get; set; } = new Dictionary<string, JsonElement>();
}

/// <summary>
/// Response returned when an inbound hook task run is queued or already running.
/// </summary>
public sealed class TriggerTaskHookResponse
{
    public string TaskId { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public AgentTaskStatus Status { get; set; }
    public string StatusUrl { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Limited task run status exposed to inbound hook API callers.
/// </summary>
public sealed class TaskHookRunStatus
{
    public string TaskId { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public AgentTaskStatus Status { get; set; }
    public string? ResultSummary { get; set; }
    public string? Error { get; set; }
    public TaskTriggerSource TriggerSource { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
