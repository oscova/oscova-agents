using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Channels;

/// <summary>Carries vendor-neutral message data and reply operations for host admission.</summary>
public sealed record ChannelMessage(
    string Text,
    string SenderId,
    string SenderDisplayName,
    string ConversationId,
    string? ThreadId,
    IReadOnlyList<ChannelAttachment> Attachments,
    Func<string, CancellationToken, Task> ReplyAsync,
    Func<IDisposable>? BeginTyping = null,
    bool IsBot = false);
