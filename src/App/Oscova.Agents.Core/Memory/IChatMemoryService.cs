using Oscova.Agents.Conversations;
using Oscova.Agents.Core.ApiContracts.Memory;
using Oscova.Agents.Core.Interfaces;

namespace Oscova.Agents.Core.Memory;

public interface IChatMemoryService
{
    Task StoreThreadAsync(string userId, string agentId, string threadId, CancellationToken ct = default);

    /// <summary>
    /// Removes all stored vector-memory chunks for a deleted chat thread.
    /// </summary>
    Task DeleteThreadAsync(string userId, string threadId, CancellationToken ct = default);

    /// <summary>Removes one thread's stored vectors and refuses later appends for that thread.</summary>
    Task SealThreadAsync(string userId, string agentId, string threadId, CancellationToken ct = default) =>
        DeleteThreadAsync(userId, threadId, ct);

    /// <summary>Lifts every seal this agent's threads carry so they can be indexed again.</summary>
    Task ReleaseAgentSealsAsync(string userId, string agentId, CancellationToken ct = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Removes all stored vector-memory chunks after the user's chat threads are cleared.
    /// </summary>
    Task<int> DeleteAllThreadsAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Removes all stored vector-memory chunks for a deleted agent.
    /// </summary>
    Task<int> DeleteByAgentAsync(string userId, string agentId, CancellationToken ct = default);

    /// <summary>Clears stored vectors and makes every chat thread eligible for indexing again.</summary>
    Task ResetIndexAsync(string userId, CancellationToken ct = default);

    /// <summary>Returns the authoritative committed display-message position for one thread.</summary>
    Task<int> GetCommittedPositionAsync(
        string userId,
        string threadId,
        CancellationToken ct = default);

    /// <summary>
    /// Stores only the supplied display messages so callers can append new memory chunks
    /// without re-indexing the entire thread.
    /// </summary>
    Task<int?> StoreMessagesAsync(
        string userId,
        string agentId,
        string threadId,
        IReadOnlyList<ChatDisplayMessage> messages,
        int expectedPosition,
        int nextPosition,
        bool requireLocalEndpoint = false,
        CancellationToken ct = default);

    Task<SemanticSearchResult<ChatMemorySearchResult>> SearchAsync(
        string userId,
        string agentId,
        string query,
        int maxResults = 5,
        bool requireLocalEndpoint = false,
        CancellationToken ct = default);

    Task<IReadOnlyList<ChatMemorySearchResult>> GetByThreadIdAsync(string userId, string agentId, string threadId, CancellationToken ct = default);

    /// <summary>
    /// Returns one page of stored chat-memory chunks for the current user, optionally filtered by agent or thread.
    /// </summary>
    Task<ChatMemoryPage> GetPageAsync(
        string userId,
        string? agentId,
        string? threadId,
        int page,
        int pageSize,
        CancellationToken ct = default);
}
