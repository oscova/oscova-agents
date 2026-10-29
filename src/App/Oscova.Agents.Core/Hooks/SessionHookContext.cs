namespace Oscova.Agents.Core.Hooks;

/// <summary>
/// Carries new chat-session metadata supplied to a session-start hook.
/// </summary>
public sealed record SessionHookContext
{
    public required string UserId { get; init; }
    public required string AgentId { get; init; }
    public required string ChatThreadId { get; init; }
    public required string ExecutionKind { get; init; }
    /// <summary>Identifies the originating channel when the run came from a channel connection.</summary>
    public string? ChannelId { get; init; }
}