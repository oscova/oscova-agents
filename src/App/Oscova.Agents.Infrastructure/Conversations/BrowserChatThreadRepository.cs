using System.Text.Json;
using Oscova.Agents.Conversations;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Interfaces;

namespace Oscova.Agents.Infrastructure.Conversations;

/// <summary>Attaches browser, chat choice and goal cleanup to individual, bulk and agent conversation deletion.</summary>
public sealed class BrowserChatThreadRepository(IChatThreadRepository inner, BrowserWorkspaceService browser,
    ConversationChoiceStore choices, IWebToolApprovalService approvals, IChatAttachmentService attachments,
    ConversationGoalStore goals) : IChatThreadRepository
{
    /// <inheritdoc />
    public async Task DeleteThreadAsync(string userId, string threadId, CancellationToken ct = default)
    {
        var thread = await inner.GetThreadAsync(userId, threadId, ct).ConfigureAwait(false);
        if (thread is not null) { await choices.DeleteAsync(userId, thread, ct).ConfigureAwait(false); }

        approvals.RevokeSessionPermission(threadId, null);
        await inner.DeleteThreadAsync(userId, threadId, ct).ConfigureAwait(false);
        // Remove goals after the authoritative thread, excluding overlapping goal creation.
        if (thread is not null) { await goals.DeleteAsync(userId, thread, CancellationToken.None).ConfigureAwait(false); }

        if (thread is not null) { attachments.DeleteThreadFolder(userId, threadId, thread.IsIncognito); }

        await browser.RemoveChatsAsync([threadId], ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> DeleteAllThreadsAsync(string userId, CancellationToken ct = default)
    {
        var threads = await inner.GetThreadsAsync(userId, ct).ConfigureAwait(false);
        var privateThreads = await inner.GetIncognitoThreadsAsync(userId, ct).ConfigureAwait(false);
        await choices.DeleteAllAsync(userId, ct).ConfigureAwait(false);
        foreach (var thread in threads.Concat(privateThreads)) { approvals.RevokeSessionPermission(thread.Id, null); }

        var count = await inner.DeleteAllThreadsAsync(userId, ct).ConfigureAwait(false);
        await goals.DeleteAllAsync(userId, CancellationToken.None).ConfigureAwait(false);
        attachments.DeleteAllThreadFolders(userId);
        await browser.RemoveChatsAsync(threads.Concat(privateThreads).Select(thread => thread.Id).ToArray(), ct).ConfigureAwait(false);
        return count;
    }

    /// <inheritdoc />
    public async Task<int> DeleteThreadsByAgentAsync(string userId, string agentId, CancellationToken ct = default)
    {
        var threads = await inner.GetThreadsAsync(userId, ct).ConfigureAwait(false);
        var privateThreads = await inner.GetIncognitoThreadsAsync(userId, ct).ConfigureAwait(false);
        var removed = threads.Concat(privateThreads).Where(thread => thread.AgentId == agentId).ToArray();
        // Agent removal and deferred cleanup bypass the chat service, so clear authority at this shared boundary.
        foreach (var thread in removed)
        {
            await choices.DeleteAsync(userId, thread, ct).ConfigureAwait(false);
            approvals.RevokeSessionPermission(thread.Id, null);
        }

        var count = await inner.DeleteThreadsByAgentAsync(userId, agentId, ct).ConfigureAwait(false);
        foreach (var thread in removed) { await goals.DeleteAsync(userId, thread, CancellationToken.None).ConfigureAwait(false); }

        foreach (var thread in removed) { attachments.DeleteThreadFolder(userId, thread.Id, thread.IsIncognito); }

        await browser.RemoveChatsAsync(removed.Select(thread => thread.Id).ToArray(), ct).ConfigureAwait(false);
        return count;
    }

    /// <inheritdoc />
    public Task<bool> TryReplaceDisplayMessageAsync(string userId, string threadId, ChatDisplayMessage message, CancellationToken ct = default) => inner.TryReplaceDisplayMessageAsync(userId, threadId, message, ct);
    /// <inheritdoc />
    public Task<IReadOnlyList<ChatThread>> GetThreadsAsync(string userId, CancellationToken ct = default) => inner.GetThreadsAsync(userId, ct);
    /// <inheritdoc />
    public Task<IReadOnlyList<ChatThread>> GetThreadsWithMessagesAsync(string userId, CancellationToken ct = default) => inner.GetThreadsWithMessagesAsync(userId, ct);
    /// <inheritdoc />
    public Task<ChatThread?> GetThreadAsync(string userId, string threadId, CancellationToken ct = default,
        int? messageLimit = null, string? beforeMessageId = null, string? firstMessageId = null) =>
        inner.GetThreadAsync(userId, threadId, ct, messageLimit, beforeMessageId, firstMessageId);
    /// <inheritdoc />
    public Task<bool> ThreadExistsAsync(string userId, string threadId, CancellationToken ct = default) => inner.ThreadExistsAsync(userId, threadId, ct);
    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetProjectThreadIdsAsync(string userId, string projectId, CancellationToken ct = default) => inner.GetProjectThreadIdsAsync(userId, projectId, ct);
    /// <inheritdoc />
    public Task<IReadOnlyList<ChatThread>> GetIncognitoThreadsAsync(string userId, CancellationToken ct = default) => inner.GetIncognitoThreadsAsync(userId, ct);
    /// <inheritdoc />
    public Task CreateThreadAsync(string userId, ChatThread thread, bool setCurrent = true, CancellationToken ct = default) => inner.CreateThreadAsync(userId, thread, setCurrent, ct);
    /// <inheritdoc />
    public Task SaveThreadAsync(string userId, ChatThread thread, bool setCurrent = true, CancellationToken ct = default) => inner.SaveThreadAsync(userId, thread, setCurrent, ct);
    /// <inheritdoc />
    public Task<bool> TryMarkThreadReadAsync(string userId, string threadId, CancellationToken ct = default) => inner.TryMarkThreadReadAsync(userId, threadId, ct);
    /// <inheritdoc />
    public Task<bool> TryRenameThreadAsync(string userId, string threadId, string title, CancellationToken ct = default) => inner.TryRenameThreadAsync(userId, threadId, title, ct);
    /// <inheritdoc />
    public Task<JsonElement?> GetSessionElementAsync(string userId, string threadId, CancellationToken ct = default) => inner.GetSessionElementAsync(userId, threadId, ct);
    /// <inheritdoc />
    public Task SaveSessionElementAsync(string userId, string threadId, JsonElement sessionElement, string? agentId, CancellationToken ct = default) => inner.SaveSessionElementAsync(userId, threadId, sessionElement, agentId, ct);
    /// <inheritdoc />
    public Task DeleteSessionElementAsync(string userId, string threadId, CancellationToken ct = default) => inner.DeleteSessionElementAsync(userId, threadId, ct);
    /// <inheritdoc />
    public Task UpdateWatermarksAsync(string userId, string threadId, int? lastIndexedMessageCount, int? lastSummarizedMessageCount, CancellationToken ct = default, int? lastReflectedMessageCount = null, int? lastSummarizedMessageOffset = null, int? lastReflectedMessageOffset = null)
        => inner.UpdateWatermarksAsync(userId, threadId, lastIndexedMessageCount, lastSummarizedMessageCount, ct, lastReflectedMessageCount, lastSummarizedMessageOffset, lastReflectedMessageOffset);
    /// <inheritdoc />
    public Task MarkCurrentAsync(string userId, string agentId, string threadId, CancellationToken ct = default) => inner.MarkCurrentAsync(userId, agentId, threadId, ct);
    /// <inheritdoc />
    public Task ClearCurrentAsync(string userId, string agentId, string? projectId, CancellationToken ct = default) => inner.ClearCurrentAsync(userId, agentId, projectId, ct);
}
