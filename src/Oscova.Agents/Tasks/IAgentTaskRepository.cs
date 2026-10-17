using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Tasks
{
    /// <summary>
    /// Persists and retrieves agent task definitions and their execution run records.
    /// </summary>
    public interface IAgentTaskRepository
    {
        /// <summary>
        /// Returns all task definitions that are currently enabled, across all users.
        /// Used by the scheduler to determine which tasks are eligible to run.
        /// </summary>
        Task<IReadOnlyList<AgentTaskDefinition>> GetAllEnabledTasksAsync(CancellationToken ct = default);

        /// <summary>
        /// Returns all task definitions belonging to the specified user, ordered by name.
        /// </summary>
        Task<IReadOnlyList<AgentTaskDefinition>> GetTasksByUserAsync(string userId, CancellationToken ct = default);

        /// <summary>
        /// Returns a single task definition by user and task ID, or <see langword="null"/> if not found.
        /// </summary>
        Task<AgentTaskDefinition?> GetTaskAsync(string userId, string taskId, CancellationToken ct = default);

        /// <summary>
        /// Returns the task whose external trigger is enabled for the specified slug, or <see langword="null"/> if none exists.
        /// </summary>
        Task<AgentTaskDefinition?> GetTaskByExternalTriggerSlugAsync(string slug, CancellationToken ct = default);

        /// <summary>
        /// Creates or updates the given task definition.
        /// </summary>
        Task SaveTaskAsync(AgentTaskDefinition task, CancellationToken ct = default, string? creationDirectoryName = null);

        /// <summary>
        /// Permanently deletes the specified task definition and its run history.
        /// </summary>
        Task DeleteTaskAsync(string userId, string taskId, CancellationToken ct = default);

        /// <summary>
        /// Returns the most recent run for the given task, or <see langword="null"/> if the task has never run.
        /// </summary>
        Task<AgentTaskRun?> GetLatestRunAsync(string userId, string taskId, CancellationToken ct = default);

        /// <summary>Returns the latest resume session across skipped runs that saved no session.</summary>
        Task<JsonElement?> GetLatestSessionAsync(string userId, string taskId, CancellationToken ct = default);

        /// <summary>Returns the most recent run without opening confidential content.</summary>
        Task<AgentTaskRun?> GetLatestRunMetadataAsync(string userId, string taskId, CancellationToken ct = default) =>
            GetLatestRunAsync(userId, taskId, ct);

        /// <summary>
        /// Returns a persisted task run by identifier, or <see langword="null"/> if not found.
        /// </summary>
        Task<AgentTaskRun?> GetRunAsync(string userId, string taskId, string runId, CancellationToken ct = default);

        /// <summary>Returns a persisted run without opening confidential content.</summary>
        Task<AgentTaskRun?> GetRunMetadataAsync(
            string userId,
            string taskId,
            string runId,
            CancellationToken ct = default) =>
            GetRunAsync(userId, taskId, runId, ct);

        /// <summary>
        /// Returns the most recent run for each of the given task IDs in a single query.
        /// Tasks that have never run are absent from the returned dictionary.
        /// </summary>
        Task<IReadOnlyDictionary<string, AgentTaskRun>> GetLatestRunsAsync(IEnumerable<string> taskIds, CancellationToken ct = default);

        /// <summary>
        /// Returns the most recent runs for the given task, newest first, up to <paramref name="limit"/> records.
        /// </summary>
        Task<IReadOnlyList<AgentTaskRun>> GetRunsAsync(string userId, string taskId, int limit = 20, CancellationToken ct = default);

        /// <summary>
        /// Creates or updates the given run record.
        /// </summary>
        Task SaveRunAsync(AgentTaskRun run, CancellationToken ct = default);
        /// <summary>Parses caller-owned task markdown before validation and saving.</summary>
        AgentTaskDefinition ParseDefinition(string ownerUserIdFromCatalog, string fileText);
    }
}