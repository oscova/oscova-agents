using Oscova.Agents.Core.Channels;
using Oscova.Agents.Conversations;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Memory;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Definitions;
using Oscova.Agents.Tasks;

namespace Oscova.Agents.Core.Secrets;

/// <summary>
/// Centralizes stale cross-entity reference cleanup after upstream entities are removed.
/// </summary>
public sealed class ReferenceCleanupService(
    IAgentTaskRepository taskRepository,
    AgentTaskSchedulerService taskScheduler,
    IChatThreadRepository chatThreadRepository,
    IChatMemoryService chatMemoryService,
    ISharedAgentPersonalContextRepository sharedContextRepository,
    IAgentTaskCatalog taskCatalog,
    IUserDirectoryService userDirectoryService,
    ICredentialService credentialService,
    HookSettings hookSettings,
    IChannelManager channelManager,
    AgentGoalSettings agentGoals)
{
    public sealed record AgentCleanupCounts(int Threads, int Tasks, int SharedContexts, int Memories);
    public sealed record TaskCleanupCounts(int HookSlugReferences);
    public sealed record UserCleanupCounts(int Threads, int Credentials, int FilesRemoved);

    /// <summary>
    /// Clears every downstream pointer to the given agent for one user.
    /// </summary>
    public async Task<AgentCleanupCounts> ClearAgentReferencesAsync(
        string userId,
        AgentDefinition agent,
        CancellationToken ct)
    {
        var clearedTaskCount = 0;
        var tasks = await taskRepository.GetTasksByUserAsync(userId, ct).ConfigureAwait(false);
        foreach (var task in tasks)
        {
            if (!string.Equals(task.AgentId, agent.Id, StringComparison.Ordinal))
            {
                continue;
            }

            var cleared = await taskScheduler.RunTaskSaveAsync(
                userId,
                task.Id,
                async saveCt =>
                {
                    var currentTask = await taskRepository
                        .GetTaskAsync(userId, task.Id, saveCt)
                        .ConfigureAwait(false);
                    if (currentTask is null ||
                        !string.Equals(currentTask.AgentId, agent.Id, StringComparison.Ordinal))
                    {
                        return null;
                    }

                    currentTask.AgentId = string.Empty;
                    await taskRepository.SaveTaskAsync(currentTask, saveCt).ConfigureAwait(false);
                    return await taskRepository
                        .GetTaskAsync(userId, task.Id, saveCt)
                        .ConfigureAwait(false);
                },
                ct).ConfigureAwait(false);
            if (cleared)
            {
                clearedTaskCount++;
            }
        }

        await agentGoals.SetLimitAsync(agent.Id, null).ConfigureAwait(false);
        var deletedThreadCount = await chatThreadRepository.DeleteThreadsByAgentAsync(userId, agent.Id, ct).ConfigureAwait(false);
        var deletedMemoryCount = await chatMemoryService.DeleteByAgentAsync(userId, agent.Id, ct).ConfigureAwait(false);
        var deletedSharedContextCount = await sharedContextRepository
            .DeleteForUsersAsync([userId], agent, ct)
            .ConfigureAwait(false);

        return new AgentCleanupCounts(
            deletedThreadCount,
            clearedTaskCount,
            deletedSharedContextCount,
            deletedMemoryCount);
    }

    /// <summary>
    /// Clears hook API-key allow-list references to a deleted task webhook slug.
    /// </summary>
    public async Task<TaskCleanupCounts> ClearTaskReferencesAsync(string taskSlug, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(taskSlug))
        {
            return new TaskCleanupCounts(0);
        }

        ct.ThrowIfCancellationRequested();

        return await hookSettings.RunExclusiveAsync(async () =>
        {
            await hookSettings.LoadAsync().ConfigureAwait(false);

            var touchedApiKeyCount = 0;
            var previousApiKeys = hookSettings.ApiKeys.ToList();
            var apiKeys = hookSettings.ApiKeys.Select(key => new HookApiKeyDefinition
            {
                Id = key.Id,
                Name = key.Name,
                IsEnabled = key.IsEnabled,
                TokenPrefix = key.TokenPrefix,
                TokenHash = key.TokenHash,
                Scopes = [.. key.Scopes],
                AllowedTaskSlugs = [.. key.AllowedTaskSlugs],
                CreatedAt = key.CreatedAt,
                LastUsedAt = key.LastUsedAt
            }).ToList();

            foreach (var apiKey in apiKeys)
            {
                var before = apiKey.AllowedTaskSlugs.Count;
                apiKey.AllowedTaskSlugs = [.. apiKey.AllowedTaskSlugs
                    .Where(slug => !string.Equals(slug, taskSlug, StringComparison.OrdinalIgnoreCase))];

                if (apiKey.AllowedTaskSlugs.Count != before)
                {
                    touchedApiKeyCount++;
                }
            }

            if (touchedApiKeyCount > 0)
            {
                ct.ThrowIfCancellationRequested();

                hookSettings.ReplaceApiKeys(apiKeys);
                try
                {
                    await hookSettings.SaveAsync().ConfigureAwait(false);
                }
                catch
                {
                    hookSettings.ReplaceApiKeys(previousApiKeys);
                    throw;
                }
            }

            return new TaskCleanupCounts(touchedApiKeyCount);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Removes runtime state and files after vector admission has closed for a deleted user.</summary>
    public async Task<UserCleanupCounts> ClearUserArtifactsAsync(string userId, CancellationToken ct)
    {
        await channelManager.StopUserAsync(userId, ct).ConfigureAwait(false);

        var deletedThreadCount = await chatThreadRepository.DeleteAllThreadsAsync(userId, ct).ConfigureAwait(false);
        var deletedCredentialCount = await credentialService.DeleteAllForUserAsync(userId, ct).ConfigureAwait(false);
        // Disposable indexes are removed with the root without authenticating their contents.
        var deletedFileCount = await userDirectoryService.DeleteUserRootAsync(userId, ct).ConfigureAwait(false);
        await taskCatalog.ReloadAsync(ct).ConfigureAwait(false);

        return new UserCleanupCounts(deletedThreadCount, deletedCredentialCount, deletedFileCount);
    }

    /// <summary>
    /// Removes shared-agent personal context files for users dropped from an allow-list.
    /// </summary>
    public async Task<int> ClearDroppedShareConsumersAsync(
        AgentDefinition agent,
        IReadOnlySet<string> droppedUserIds,
        CancellationToken ct)
    {
        return droppedUserIds.Count == 0
            ? 0
            : await sharedContextRepository.DeleteForUsersAsync(droppedUserIds, agent, ct).ConfigureAwait(false);
    }
}
