using System.Text.Json;

namespace Oscova.Agents.Core.Hooks;

/// <summary>
/// Carries tool invocation metadata supplied to pre- and post-tool hooks.
/// </summary>
public sealed record ToolHookContext
{
    public required string UserId { get; init; }
    public required string AgentId { get; init; }
    public string? ChatThreadId { get; init; }
    public string? TaskId { get; init; }
    public string? TaskRunId { get; init; }
    public required string ExecutionKind { get; init; }
    public required string ToolName { get; init; }
    public string? ToolCallId { get; init; }
    public required JsonElement Arguments { get; init; }
    public string? ResultKind { get; init; }
    public string? ErrorMessage { get; init; }
    public string? Reason { get; init; }
}
