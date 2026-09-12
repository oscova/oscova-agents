using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Conversations;

/// <summary>Persists and retrieves user-owned chat threads and framework session state.</summary>
public interface IChatThreadRepository
{
    /// <summary>Returns the user's persisted chat-thread summaries. Incognito threads are excluded.</summary>
    Task<IReadOnlyList<ChatThread>> GetThreadsAsync(
        string userId,
        bool includeEncrypted = true,
        CancellationToken ct = default);
    /// <summary>Returns one chat thread when it exists, resolving in-memory incognito threads first.</summary>
    Task<ChatThread?> GetThreadAsync(string userId, string threadId, CancellationToken ct = default);
    /// <summary>Returns the user's in-memory incognito threads.</summary>
    Task<IReadOnlyList<ChatThread>> GetIncognitoThreadsAsync(string userId, CancellationToken ct = default);
    /// <summary>Persists a chat thread and optionally marks it current.</summary>
    Task SaveThreadAsync(string userId, ChatThread thread, bool setCurrent = true, CancellationToken ct = default);
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
        int? lastReflectedMessageCount = null);
    /// <summary>Marks a thread as current for an agent.</summary>
    Task MarkCurrentAsync(string userId, string agentId, string threadId, CancellationToken ct = default);
    /// <summary>Clears the current thread selection for an agent.</summary>
    Task ClearCurrentAsync(string userId, string agentId, CancellationToken ct = default);
    /// <summary>Deletes one persisted chat thread.</summary>
    Task DeleteThreadAsync(string userId, string threadId, CancellationToken ct = default);
    /// <summary>Deletes all persisted chat threads for a user.</summary>
    Task<int> DeleteAllThreadsAsync(string userId, CancellationToken ct = default);
    /// <summary>Deletes all persisted chat threads for an agent.</summary>
    Task<int> DeleteThreadsByAgentAsync(string userId, string agentId, CancellationToken ct = default);
}
