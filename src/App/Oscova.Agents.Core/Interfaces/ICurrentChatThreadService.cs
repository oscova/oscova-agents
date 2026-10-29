using Oscova.Agents.Conversations;

namespace Oscova.Agents.Core.Interfaces;

/// <summary>
/// Tracks which ChatThread is active for the current user and a given agent,
/// so that any channel (Web UI, Discord, Telegram) can resume the same conversation.
/// </summary>
public interface ICurrentChatThreadService
{
    /// <summary>
    /// Returns the active ChatThread for the current user and the given agent.
    /// Creates a new thread if no active (non-expired) thread exists.
    /// A new thread has no SessionElement — the caller creates a fresh agent session
    /// via agent.CreateSessionAsync().
    /// </summary>
    Task<ChatThread> GetOrCreateCurrentThreadAsync(string agentId, string? projectId, CancellationToken ct = default);

    /// <summary>Finds the selected current thread without applying idle-resume expiry or creating a thread.</summary>
    Task<ChatThread?> GetCurrentThreadAsync(string agentId, string? projectId, CancellationToken ct = default);

    /// <summary>Finds the most recent resumable current chat, optionally limiting display hydration.</summary>
    Task<ChatThread?> GetRecentCurrentThreadAsync(CancellationToken ct = default, int? messageLimit = null);

    /// <summary>Finds a resumable chat for the selected agent and project, optionally limiting display hydration.</summary>
    Task<ChatThread?> GetRecentCurrentThreadAsync(string agentId, string? projectId, CancellationToken ct = default, int? messageLimit = null);

    /// <summary>
    /// Closes the current thread so the next call to GetOrCreateCurrentThreadAsync
    /// starts a fresh one. Call this when the user clicks "New Chat".
    /// </summary>
    Task StartNewConversationAsync(string agentId, string? projectId, CancellationToken ct = default);
}
