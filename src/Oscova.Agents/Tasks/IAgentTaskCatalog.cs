using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Tasks;

/// <summary>Indexes file-backed tasks; the first-loaded duplicate ID wins and later entries are skipped with a warning.</summary>
public interface IAgentTaskCatalog
{
    /// <summary>Resolves a task id to the directory containing its task.md file, or null when unknown.</summary>
    string? TryGetTaskDirectory(string taskId);

    /// <summary>Resolves a task id to the user id that owns the on-disk task directory.</summary>
    string? TryGetOwnerUserId(string taskId);

    /// <summary>Resolves a task id to a built-in task handler key when the task lives in a configured folder.</summary>
    string? TryGetBuiltInTaskKey(string taskId);

    /// <summary>Returns all catalogued task definitions physically owned by <paramref name="userId"/>.</summary>
    IReadOnlyCollection<AgentTaskCatalogEntry> GetTasksForUser(string userId);

    /// <summary>Returns all catalogued task definitions that are enabled for scheduling.</summary>
    IReadOnlyCollection<AgentTaskCatalogEntry> GetAllEnabledTasks();

    /// <summary>Rebuilds the catalogue from the filesystem.</summary>
    Task ReloadAsync(CancellationToken ct = default);

    /// <summary>Rebuilds only the catalogue slice owned by <paramref name="userId"/>.</summary>
    Task ReloadUserAsync(string userId, CancellationToken ct = default);
}

/// <summary>Describes task metadata needed for catalogue lookup and filtering.</summary>
public sealed record AgentTaskCatalogEntry(
    string TaskId,
    string OwnerUserId,
    string DirectoryPath,
    bool IsEnabled,
    string? BuiltInTaskKey);
