namespace Oscova.Agents.Core.Conversation;

/// <summary>Identifies a pending reply within one host-owned connection.</summary>
public sealed record PluginReplyKey(string OwnerId, string PluginId, string ChannelId, Guid ConnectionId,
    string ConversationId, string SenderId, string? ThreadId);

/// <summary>Binds an elicitation prompt to the original connection's reply callback.</summary>
public sealed record PluginElicitationOrigin(PluginReplyKey Key, Func<string, CancellationToken, Task> SendAsync)
{
    /// <summary>Stops the exact run that owns this prompt when its conversation is replaced.</summary>
    public Action? CancelRun { get; init; }
}
