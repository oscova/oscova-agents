using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Conversation;

namespace Oscova.Agents.Infrastructure.Conversations;

/// <summary>Correlates plugin channel replies within their owning connection and conversation.</summary>
public sealed class PluginTextElicitationTransport(ILogger<PluginTextElicitationTransport> logger)
{
    private readonly PendingTextReplies<PluginReplyKey> _replies = new();

    /// <summary>Registers the reply waiter before sending through the originating connection.</summary>
    public Task<string?> SendPromptAndAwaitReplyAsync(PluginElicitationOrigin origin, string prompt, CancellationToken cancellationToken)
    {
        return _replies.SendAndAwaitAsync(origin.Key,
            ct => origin.SendAsync(prompt, ct), cancellationToken, origin.CancelRun);
    }

    /// <summary>Removes the conversation's pending prompt and stops its owning run.</summary>
    public void CancelPendingReply(PluginReplyKey key)
    {
        // Slash commands can omit the originating message's thread ID.
        _replies.Cancel(candidate => candidate with { ThreadId = key.ThreadId } == key);
    }

    /// <summary>Sends an acknowledgement while retaining non-fatal transport diagnostics.</summary>
    public async Task SendAcknowledgementAsync(PluginElicitationOrigin origin, string text, CancellationToken cancellationToken)
    {
        try { await origin.SendAsync(text, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Channel acknowledgement failed for user {UserId} on {ChannelId}.", origin.Key.OwnerId, origin.Key.ChannelId);
        }
    }

    /// <summary>Consumes a reply only when every owner, connection, and conversation field matches.</summary>
    public bool TryResolveReply(PluginReplyKey key, string text, bool isBot) =>
        isBot ? _replies.HasPending(key) : _replies.TryResolve(key, text);
}
