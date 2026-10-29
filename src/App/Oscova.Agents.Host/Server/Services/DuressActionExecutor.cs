using Oscova.Agents.Conversations;
using Oscova.Agents.Core;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Host.Server.Chat;
using Oscova.Agents.Infrastructure.Auth;
using Oscova.Agents.Infrastructure.Secrets;
using Oscova.Agents.Infrastructure.Users;

namespace Oscova.Agents.Host.Server.Services;

/// <summary>Runs the sealed, retry-safe destruction policy for a duress sign-in.</summary>
public sealed class DuressActionExecutor(
    UserServiceManager userServiceManager,
    IUserDirectoryService userDirectoryService,
    ChatSessionService chatSessionService,
    ThreadRunGuard threadRunGuard,
    ChatRunProjectionStore projectionStore)
{
    /// <summary>Returns true only when every configured duress action completes.</summary>
    public Task<bool> ExecuteAsync(ApplicationUser user, DuressActions actions) =>
        TryAsync(() => RunSelectedActionsAsync(user, actions));

    private async Task<bool> RunSelectedActionsAsync(ApplicationUser user, DuressActions actions)
    {
        var succeeded = true;
        var scope = await userServiceManager
            .GetOrCreateScopeAsync(user.Id, CancellationToken.None)
            .ConfigureAwait(false);
        using var lease = await threadRunGuard
            .CancelAndAcquireUserAsync(user.Id, CancellationToken.None)
            .ConfigureAwait(false);

        foreach (var agentId in actions.AgentIds)
        {
            var agent = scope.AppService.CustomAgentDefinitions.FirstOrDefault(existing =>
                string.Equals(existing.Id, agentId, StringComparison.Ordinal));
            if (agent is not { IsShared: false } ||
                !string.Equals(agent.OwnerUserId, user.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            succeeded &= await TryAsync(() => scope.AppService.DeleteCustomAgentAsync(agentId)).ConfigureAwait(false);
        }

        // Duress targets stable GUID identities; recreating a deleted task with the same ID is unsupported.
        foreach (var taskId in actions.TaskIds)
        {
            succeeded &= await TryAsync(
                () => scope.TaskManagementService.DeleteAsync(taskId, CancellationToken.None))
                .ConfigureAwait(false);
        }

        foreach (var workspace in actions.Workspaces)
        {
            if (!string.Equals(
                workspace,
                UserFileNames.DefaultWorkspaceDirectory,
                StringComparison.OrdinalIgnoreCase))
            {
                succeeded &= await TryAsync(() => ClearWorkspaceContentsAsync(user.Id, workspace))
                    .ConfigureAwait(false);
            }
        }

        if (actions.ClearSharedFolder)
        {
            // Entries members hold open never fail the sign-in, and an unreachable folder must not skip the actions after it.
            _ = await TryAsync(() =>
            {
                ClearSharedFolder(userDirectoryService.GetSharedDirectoryPath());
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }

        if (actions.DeleteAllThreads)
        {
            succeeded &= await TryAsync(
                () => chatSessionService.DeleteAllThreadsAsync(user.Id, CancellationToken.None))
                .ConfigureAwait(false);
            projectionStore.RemoveUser(user.Id);
        }

        return succeeded;
    }

    private Task<bool> ClearWorkspaceContentsAsync(string userId, string workspaceName)
    {
        var path = userDirectoryService.GetWorkspaceDirectoryPath(userId, workspaceName);
        if (!Directory.Exists(path))
        {
            return Task.FromResult(true);
        }

        var succeeded = true;
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            try
            {
                if (Directory.Exists(entry))
                {
                    Directory.Delete(entry, recursive: true);
                }
                else
                {
                    File.Delete(entry);
                }
            }
            catch
            {
                succeeded = false;
            }
        }

        return Task.FromResult(succeeded);
    }

    // Never waits for other members so a panic sign-in stays fast and gives no hint.
    // An entry another member holds open stays behind instead of failing the sign-in.
    private static void ClearSharedFolder(string directory)
    {
        List<string> entries;
        try
        {
            entries = [.. Directory.EnumerateFileSystemEntries(directory)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var entry in entries)
        {
            try
            {
                if (Directory.Exists(entry))
                {
                    Directory.Delete(entry, recursive: true);
                }
                else
                {
                    File.Delete(entry);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A folder that failed part way keeps only the entries still held open, never a link's target.
                if (Directory.Exists(entry) && new DirectoryInfo(entry).LinkTarget is null)
                {
                    ClearSharedFolder(entry);
                }
            }
        }
    }

    private static async Task<bool> TryAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> TryAsync(Func<Task<bool>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }
}
