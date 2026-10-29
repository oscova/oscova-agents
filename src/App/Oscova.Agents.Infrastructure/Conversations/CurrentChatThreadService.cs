using Oscova.Agents.Conversations;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Settings;

namespace Oscova.Agents.Infrastructure.Conversations;

public sealed class CurrentChatThreadService(
    IChatThreadRepository chatThreadRepository,
    AssistantUiSettings uiSettings,
    string userId) : ICurrentChatThreadService
{
    public async Task<ChatThread> GetOrCreateCurrentThreadAsync(string agentId, string? projectId, CancellationToken ct = default)
    {
        var current = await GetRecentCurrentThreadAsync(agentId, projectId, ct).ConfigureAwait(false);
        if (current is not null)
        {
            return current;
        }

        var newThread = new ChatThread { AgentId = agentId, ProjectId = projectId };
        await chatThreadRepository.CreateThreadAsync(userId, newThread, ct: ct).ConfigureAwait(false);
        return newThread;
    }

    /// <summary>
    /// Finds the newest current thread across agents that still has a recent user-authored message.
    /// </summary>
    public Task<ChatThread?> GetRecentCurrentThreadAsync(CancellationToken ct = default, int? messageLimit = null)
    {
        return FindRecentCurrentThreadAsync(agentId: null, projectId: null, ct, messageLimit);
    }

    public async Task StartNewConversationAsync(string agentId, string? projectId, CancellationToken ct = default)
    {
        await chatThreadRepository.ClearCurrentAsync(userId, agentId, projectId, ct).ConfigureAwait(false);
    }

    public Task<ChatThread?> GetRecentCurrentThreadAsync(string agentId, string? projectId, CancellationToken ct = default, int? messageLimit = null)
        => FindRecentCurrentThreadAsync(agentId, projectId, ct, messageLimit);

    /// <summary>Finds the selected current thread without applying the idle-resume window.</summary>
    public async Task<ChatThread?> GetCurrentThreadAsync(string agentId, string? projectId, CancellationToken ct = default)
    {
        var summary = (await GetCurrentThreadsAsync(agentId, projectId, ct).ConfigureAwait(false)).FirstOrDefault();
        return summary is null ? null : await chatThreadRepository.GetThreadAsync(userId, summary.Id, ct).ConfigureAwait(false);
    }

    // Keeps current selection matching identical for live routing and idle resume.
    private async Task<IReadOnlyList<ChatThread>> GetCurrentThreadsAsync(string? agentId, string? projectId, CancellationToken ct)
    {
        return (await chatThreadRepository.GetThreadsAsync(userId, ct: ct).ConfigureAwait(false))
            .Where(thread => thread.IsCurrent && !string.IsNullOrWhiteSpace(thread.AgentId) &&
                (agentId is null || string.Equals(thread.AgentId, agentId, StringComparison.Ordinal) &&
                    string.Equals(thread.ProjectId, projectId, StringComparison.Ordinal)))
            .ToList();
    }

    private async Task<ChatThread?> FindRecentCurrentThreadAsync(string? agentId, string? projectId, CancellationToken ct, int? messageLimit = null)
    {
        var windowMinutes = AssistantUiSettings.NormalizeChatResumeWindowMinutes(uiSettings.ChatResumeWindowMinutes);
        if (windowMinutes == 0)
        {
            return null;
        }

        var window = TimeSpan.FromMinutes(windowMinutes);
        var summaries = await GetCurrentThreadsAsync(agentId, projectId, ct).ConfigureAwait(false);

        ChatThread? bestThread = null;
        DateTimeOffset? bestUserMessageAt = null;
        foreach (var summary in summaries)
        {
            var thread = await chatThreadRepository.GetThreadAsync(userId, summary.Id, ct, messageLimit).ConfigureAwait(false);
            if (thread is null)
            {
                continue;
            }

            // A selected handoff has a persisted session before its first user reply.
            var latestUserMessageAt = GetLatestUserMessageAt(thread)
                ?? (thread.SessionElement.HasValue ? thread.DisplayMessages
                    .Where(message => message.From == MessageOwner.Bot)
                    .Max(message => (DateTimeOffset?)message.CreatedAt) : null);
            if (latestUserMessageAt is null)
            {
                continue;
            }

            var isExpired = DateTimeOffset.UtcNow - latestUserMessageAt.Value > window;
            if (isExpired)
            {
                await chatThreadRepository.ClearCurrentAsync(userId, summary.AgentId!, summary.ProjectId, ct).ConfigureAwait(false);
                continue;
            }

            if (bestUserMessageAt is null || latestUserMessageAt > bestUserMessageAt)
            {
                bestThread = thread;
                bestUserMessageAt = latestUserMessageAt;
            }
        }

        return bestThread;
    }

    private static DateTimeOffset? GetLatestUserMessageAt(ChatThread thread)
    {
        return thread.DisplayMessages
            .Where(message => message.From == MessageOwner.User)
            .Max(message => (DateTimeOffset?)message.CreatedAt);
    }
}
