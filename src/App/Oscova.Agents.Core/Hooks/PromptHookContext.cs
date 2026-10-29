using Oscova.Agents.Core.ApiContracts.Chat;

namespace Oscova.Agents.Core.Hooks;

/// <summary>
/// Carries the user prompt data supplied to a blocking prompt hook.
/// </summary>
public sealed record PromptHookContext
{
    public required string UserId { get; init; }
    public required string AgentId { get; init; }
    public required string ChatThreadId { get; init; }
    public required string ExecutionKind { get; init; }
    /// <summary>Identifies the originating channel when the run came from a channel connection.</summary>
    public string? ChannelId { get; init; }
    public required string PromptText { get; init; }
    public IReadOnlyList<ChatAttachmentInfo> Attachments { get; init; } = [];
}