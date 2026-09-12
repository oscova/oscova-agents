namespace Oscova.Agents.Tasks;

/// <summary>
/// Notification payload for task status changes.
/// </summary>
/// <param name="TaskId">The task definition identifier.</param>
/// <param name="TaskName">The user-facing task name.</param>
/// <param name="Status">The current task run status.</param>
/// <param name="Message">An optional status message or error.</param>
public sealed record TaskStatusUpdate(
    string TaskId,
    string TaskName,
    AgentTaskStatus Status,
    string? Message = null);
