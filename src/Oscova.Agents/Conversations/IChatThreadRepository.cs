using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Conversations;

/// <summary>Persists and retrieves user-owned chat threads and framework session state.</summary>
public interface IChatThreadRepository
{
    /// <summary>Replaces one existing display message without changing its model session.</summary>
    Task<bool> TryReplaceDisplayMessageAsync(string userId, string threadId,
        ChatDisplayMessage message, CancellationToken ct = default);

    /// <summary>Returns the user's persisted chat-thread summaries. Incognito threads are excluded.</summary>
    Task<IReadOnlyList<ChatThread>> GetThreadsAsync(string userId, CancellationToken ct = default);
    /// <summary>Returns owned persisted conversations with display messages, excluding model session state.</summary>
    Task<IReadOnlyList<ChatThread>> GetThreadsWithMessagesAsync(string userId, CancellationToken ct = default);
    /// <summary>Returns an owned thread, optionally paging complete display exchanges without trimming its model session.</summary>
    /// <param name="userId">The owning user.</param>
    /// <param name="threadId">The conversation identity.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <param name="messageLimit">Positive target display count, or null for complete history. Exchanges may exceed the target.</param>
    /// <param name="beforeMessageId">Existing message excluded from an older page, requiring messageLimit.</param>
    /// <param name="firstMessageId">Existing first message retained when refreshing a loaded window, requiring messageLimit and no beforeMessageId.</param>
    Task<ChatThread?> GetThreadAsync(string userId, string threadId, CancellationToken ct = default,
        int? messageLimit = null, string? beforeMessageId = null, string? firstMessageId = null);
    /// <summary>Checks ownership using unlocked persisted summaries or in-memory incognito entries.</summary>
    Task<bool> ThreadExistsAsync(string userId, string threadId, CancellationToken ct = default);
    /// <summary>Returns owned project thread identities from unlocked summaries and in-memory incognito entries.</summary>
    Task<IReadOnlyList<string>> GetProjectThreadIdsAsync(string userId, string projectId, CancellationToken ct = default);
    /// <summary>Returns the user's in-memory incognito threads.</summary>
    Task<IReadOnlyList<ChatThread>> GetIncognitoThreadsAsync(string userId, CancellationToken ct = default);
    /// <summary>Persists a chat thread and optionally marks it current.</summary>
    /// <summary>Creates a conversation before subsequent saves can update it.</summary>
    Task CreateThreadAsync(string userId, ChatThread thread, bool setCurrent = true, CancellationToken ct = default);
    /// <summary>Updates a conversation that still exists without recreating deleted state.</summary>
    Task SaveThreadAsync(string userId, ChatThread thread, bool setCurrent = true, CancellationToken ct = default);
    /// <summary>Marks an existing conversation read without rewriting its contents.</summary>
    Task<bool> TryMarkThreadReadAsync(string userId, string threadId, CancellationToken ct = default);
    /// <summary>Renames an existing chat thread without creating missing state.</summary>
    Task<bool> TryRenameThreadAsync(string userId, string threadId, string title, CancellationToken ct = default);
    /// <summary>Returns the serialized framework session state for a thread.</summary>
    Task<JsonElement?> GetSessionElementAsync(string userId, string threadId, CancellationToken ct = default);
    /// <summary>Persists serialized framework session state for a thread.</summary>
    Task SaveSessionElementAsync(string userId, string threadId, JsonElement sessionElement, string? agentId, CancellationToken ct = default);
    /// <summary>Deletes the serialized framework session state for a thread.</summary>
    Task DeleteSessionElementAsync(string userId, string threadId, CancellationToken ct = default);
    /// <summary>Updates the persisted processing watermarks for a thread.</summary>
    Task UpdateWatermarksAsync(
        string userId,
        string threadId,
        int? lastIndexedMessageCount,
        int? lastSummarizedMessageCount,
        CancellationToken ct = default,
        int? lastReflectedMessageCount = null,
        int? lastSummarizedMessageOffset = null,
        int? lastReflectedMessageOffset = null);
    /// <summary>Marks a thread as current for an agent.</summary>
    Task MarkCurrentAsync(string userId, string agentId, string threadId, CancellationToken ct = default);
    /// <summary>Clears the current thread selection for an agent.</summary>
    Task ClearCurrentAsync(string userId, string agentId, string? projectId, CancellationToken ct = default);
    /// <summary>Deletes one persisted chat thread.</summary>
    Task DeleteThreadAsync(string userId, string threadId, CancellationToken ct = default);
    /// <summary>Deletes all persisted chat threads for a user.</summary>
    Task<int> DeleteAllThreadsAsync(string userId, CancellationToken ct = default);
    /// <summary>Deletes all persisted chat threads for an agent.</summary>
    Task<int> DeleteThreadsByAgentAsync(string userId, string agentId, CancellationToken ct = default);
}
