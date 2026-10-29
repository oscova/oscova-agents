using Oscova.Agents.Conversations;
using Oscova.Agents.Core.Memory;

namespace Oscova.Agents.Infrastructure.Memory;

/// <summary>
/// Indexes new display messages into the per-user vector store and advances the indexing watermark.
/// </summary>
public sealed class ConversationIndexingService(
    IChatMemoryService chatMemoryService,
    IChatThreadRepository chatThreadRepository)
{
    /// <summary>
    /// Indexes only display messages that have not yet been written to the vector store.
    /// </summary>
    public async Task IndexUnflushedMessagesAsync(
        string userId,
        string agentId,
        ChatThread thread,
        bool isMemoryEnabled,
        bool requireLocalEndpoint = false,
        CancellationToken ct = default)
    {
        if (!isMemoryEnabled)
        {
            return;
        }

        var messageCount = thread.DisplayMessages.Count;
        int committedPosition = await chatMemoryService.GetCommittedPositionAsync(
            userId,
            thread.Id,
            ct).ConfigureAwait(false);
        if (messageCount <= committedPosition)
        {
            await HealWatermarkAsync(userId, thread, committedPosition, ct).ConfigureAwait(false);
            return;
        }

        var unindexedMessages = thread.DisplayMessages
            .Skip(committedPosition)
            .ToList();

        int? indexedPosition = await chatMemoryService.StoreMessagesAsync(
            userId,
            agentId,
            thread.Id,
            unindexedMessages,
            committedPosition,
            messageCount,
            requireLocalEndpoint: requireLocalEndpoint,
            ct: ct).ConfigureAwait(false);

        if (!indexedPosition.HasValue)
        {
            return;
        }

        await HealWatermarkAsync(userId, thread, indexedPosition.Value, ct).ConfigureAwait(false);
    }

    private async Task HealWatermarkAsync(
        string userId,
        ChatThread thread,
        int committedPosition,
        CancellationToken ct)
    {
        if (thread.LastIndexedMessageCount == committedPosition)
        {
            return;
        }

        thread.LastIndexedMessageCount = committedPosition;
        await chatThreadRepository.UpdateWatermarksAsync(
            userId,
            thread.Id,
            lastIndexedMessageCount: committedPosition,
            lastSummarizedMessageCount: null,
            ct).ConfigureAwait(false);
    }
}
