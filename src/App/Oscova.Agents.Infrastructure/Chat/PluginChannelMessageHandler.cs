using Microsoft.Extensions.Logging;
using Oscova.Agents.Channels;
using Oscova.Agents.Conversations;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Hooks;
using Oscova.Agents.Infrastructure.Conversations;
using Oscova.Agents.Infrastructure.Users;

namespace Oscova.Agents.Infrastructure.Chat;

/// <summary>Owns admitted work for one plugin connection while keeping conversation policy private.</summary>
public sealed class PluginChannelMessageHandler(
    string pluginId,
    ChannelDefinition channel,
    string? agentId,
    UserServiceScope userScope,
    PluginTextElicitationTransport replies,
    TextChannelTurnProcessor turnProcessor,
    IChatAttachmentService attachments,
    ILogger<PluginChannelMessageHandler> logger) : IAsyncDisposable
{
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly Lock _gate = new();
    private readonly HashSet<Task> _entered = [];
    private readonly CancellationTokenSource _lifetime = new();
    private bool _stopping;

    /// <summary>Consumes a pending reply or schedules owned turn work before returning to the vendor.</summary>
    public Task ReceiveAsync(ChannelMessage message, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_stopping || ct.IsCancellationRequested) { return Task.CompletedTask; }

            var key = new PluginReplyKey(userScope.UserId, pluginId, channel.Id, _connectionId,
                message.ConversationId, message.SenderId, message.ThreadId);
            // A fresh-conversation command wins over answers to either kind of pending prompt.
            if (!message.IsBot && TextChannelTurnProcessor.IsNewConversation(message.Text))
            {
                replies.CancelPendingReply(key);
            }
            else if (replies.TryResolveReply(key, message.Text, message.IsBot)) { return Task.CompletedTask; }

            var origin = new PluginElicitationOrigin(key, message.ReplyAsync);
            var work = Task.Run(() => ProcessAsync(message, origin));
            _entered.Add(work);
            _ = work.ContinueWith(_ =>
            {
                lock (_gate) { _entered.Remove(work); }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return Task.CompletedTask;
        }
    }

    private async Task ProcessAsync(ChannelMessage message, PluginElicitationOrigin origin)
    {
        try
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            using var typing = message.BeginTyping?.Invoke();
            var files = message.Attachments.Take(ChatAttachmentLimits.MaxFilesPerMessage).ToArray();
            var raw = files.Select(file => ChannelAttachmentPreflight.Map(file.FileName, file.ContentType, file.Size)).ToArray();
            var response = await turnProcessor.ProcessAsync(new TextChannelTurnInput(
                ElicitationChannel.Plugin, userScope.UserId, userScope, message.Text, raw, origin, message.SenderId,
                (threadId, ct) => StageAsync(threadId, files, ct), message.ReplyAsync,
                TextChannelRoute.ForPlugin(channel.Id, channel.Title, agentId)), _lifetime.Token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(response))
            {
                await message.ReplyAsync(response, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (HookDeniedException ex)
        {
            try
            {
                await message.ReplyAsync(string.IsNullOrWhiteSpace(ex.Message)
                    ? "That request was blocked by a configured hook." : ex.Message, _lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception sendError) { logger.LogWarning(sendError, "Channel hook denial could not be delivered on {ChannelId}.", channel.Id); }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Channel dispatch failed for user {UserId} on {ChannelId}.", userScope.UserId, channel.Id);
        }
    }

    private async Task<List<ChatAttachment>> StageAsync(string threadId, IReadOnlyList<ChannelAttachment> files, CancellationToken ct)
    {
        var staged = new List<ChatAttachment>();
        if (files.Count == 0) { return staged; }

        var thread = await userScope.ChatThreadRepository.GetThreadAsync(userScope.UserId, threadId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The channel conversation is no longer available.");

        foreach (var file in files)
        {
            if (!ChatAttachmentLimits.AllowedExtensions.ContainsKey(Path.GetExtension(file.FileName)) ||
                file.Size > ChatAttachmentLimits.MaxFileBytes) { continue; }

            try
            {
                var stream = await file.OpenReadAsync(ct).ConfigureAwait(false);
                await using (stream.ConfigureAwait(false))
                {
                    staged.Add(await attachments.StageAsync(userScope.UserId, threadId, file.FileName,
                        string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                        stream, file.Size, thread.IsIncognito, ct).ConfigureAwait(false));
                }
            }
            catch (ChatAttachmentValidationException ex)
            {
                logger.LogWarning("Rejected attachment {FileName} on {ChannelId}: {Reason}", file.FileName, channel.Id, ex.Message);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Attachment staging failed for user {UserId} on {ChannelId}.", userScope.UserId, channel.Id);
                ChannelAttachmentPreflight.RollbackStaged(attachments, logger, userScope.UserId, staged);
                throw;
            }
        }

        return staged;
    }

    /// <summary>Closes admission and cancels and drains all work before the transport is disposed.</summary>
    public async ValueTask DisposeAsync()
    {
        Task[] entered;
        lock (_gate)
        {
            _stopping = true;
            entered = [.. _entered];
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(entered).ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
