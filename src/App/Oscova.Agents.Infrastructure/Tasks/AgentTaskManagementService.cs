using Cronos;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Secrets;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Persistence;
using Oscova.Agents.Policies;
using Oscova.Agents.Tasks;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Infrastructure.Tasks;

/// <summary>
/// Owns task validation, persistence, deletion cleanup, and manual triggering for one user.
/// </summary>
public sealed class AgentTaskManagementService(
    string userId,
    IAgentTaskRepository taskRepository,
    AgentTaskSchedulerService taskScheduler,
    TaskRunScopeResolver taskRunScopeResolver,
    IUserDirectoryService userDirectoryService,
    ModelProviderSettings modelProviderSettings,
    AppService appService,
    AgentCompositionService crewService,
    ReferenceCleanupService referenceCleanupService,
    ILogger<AgentTaskManagementService> logger)
{
    /// <summary>
    /// Validates, sanitizes, and saves a task while preserving stored creation metadata on update.
    /// </summary>
    public async Task<(AgentTaskDefinition? Task, string? Error)> SaveAsync(
        AgentTaskDefinition request,
        AgentTaskDefinition? existing,
        string? createdByAgentId,
        CancellationToken ct, string? creationDirectoryName = null, bool preserveEnabled = false)
    {
        var taskId = existing?.Id
            ?? (string.IsNullOrWhiteSpace(request.Id) ? Guid.NewGuid().ToString() : request.Id);
        var existingTasks = await taskRepository
            .GetTasksByUserAsync(userId, ct)
            .ConfigureAwait(false);

        var (runScope, runScopeError) = ValidateRunScope(request.RunScope, request.AgentId, taskId);
        if (runScopeError is not null)
        {
            return (null, runScopeError);
        }

        // Source identity comes from provisioning, never from the submitted definition.
        request.InstructionSource = existing?.InstructionSource;
        if (request.InstructionSource is null) { request.InstructionMode = null; }
        else
        {
            try { InstructionSelection.Validate(request.InstructionMode); }
            catch (InvalidOperationException ex) { return (null, ex.Message); }
        }

        var (task, error) = ValidateAndSanitizeTask(request, taskId, existingTasks, runScope);
        if (task is null)
        {
            return (null, error);
        }

        // The creator marker is trusted separately; updates keep both stored creation fields.
        task.CreatedAt = existing?.CreatedAt ?? DateTimeOffset.UtcNow;
        task.CreatedByAgentId = existing is null ? createdByAgentId : existing.CreatedByAgentId;

        return await TrySaveTaskAsync(task, existing, ct, creationDirectoryName, preserveEnabled).ConfigureAwait(false);
    }

    /// <summary>Changes only enabled state on the current definition under the scheduler mutation gate.</summary>
    public async Task<AgentTaskDefinition?> SetEnabledAsync(string taskId, bool isEnabled, CancellationToken ct)
    {
        AgentTaskDefinition? saved = null;
        await taskScheduler.RunTaskSaveAsync(userId, taskId, async saveCt =>
        {
            var current = await taskRepository.GetTaskAsync(userId, taskId, saveCt).ConfigureAwait(false);
            if (current is null) { return null; }
            // A stale directory row must not overwrite edits to other task fields.
            current.IsEnabled = isEnabled;
            await taskRepository.SaveTaskAsync(current, saveCt).ConfigureAwait(false);
            saved = await taskRepository.GetTaskAsync(userId, taskId, saveCt).ConfigureAwait(false);
            return saved;
        }, ct).ConfigureAwait(false);
        return saved;
    }

    /// <summary>
    /// Deletes a task, clears webhook references, and returns the deleted definition when found.
    /// </summary>
    public async Task<AgentTaskDefinition?> DeleteAsync(string taskId, CancellationToken ct)
    {
        AgentTaskDefinition? task = null;
        string? taskSlug = null;
        ReferenceCleanupService.TaskCleanupCounts? cleanupCounts = null;
        await taskScheduler.RunTaskDeletionAsync(
            userId,
            taskId,
            async deleteCt =>
            {
                task = await taskRepository.GetTaskAsync(userId, taskId, deleteCt).ConfigureAwait(false);
                if (task is null)
                {
                    return false;
                }

                taskSlug = task.ExternalTrigger.Slug;
                await taskRepository.DeleteTaskAsync(userId, taskId, deleteCt).ConfigureAwait(false);
                return true;
            },
            ct,
            async cleanupCt =>
            {
                if (!string.IsNullOrWhiteSpace(taskSlug))
                {
                    cleanupCounts = await referenceCleanupService
                        .ClearTaskReferencesAsync(taskSlug, cleanupCt)
                        .ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

        if (cleanupCounts?.HookSlugReferences > 0)
        {
            logger.LogInformation(
                "Cleared task slug {Slug} from {Count} hook api-key allow-lists.",
                taskSlug,
                cleanupCounts.HookSlugReferences);
        }

        return task;
    }

    /// <summary>
    /// Starts a user-approved manual run through the scheduler.
    /// </summary>
    public Task<TaskTriggerOutcome> TriggerAsync(AgentTaskDefinition task, CancellationToken ct)
        => taskScheduler.TriggerTaskAsync(task, TaskTriggerSource.Manual, ct: ct);

    private (AgentTaskDefinition? Task, string? Error) ValidateAndSanitizeTask(
        AgentTaskDefinition request,
        string taskId,
        IReadOnlyList<AgentTaskDefinition> existingTasks,
        TaskRunScope? runScope)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return (null, "Task name is required.");
        }

        if (request.InstructionSource is null && string.IsNullOrWhiteSpace(request.Instruction))
        {
            return (null, "Task instruction is required.");
        }

        if (request.Schedule is null)
        {
            return (null, "A task schedule is required.");
        }

        var scheduleValidation = ValidateSchedule(
            request.Schedule.Kind,
            request.Schedule.IntervalMinutes,
            request.Schedule.RunAt.HasValue,
            request.Schedule.CronExpression,
            request.Schedule.PredecessorTaskId);
        if (scheduleValidation.BasicError is not null)
        {
            return (null, scheduleValidation.BasicError);
        }

        if (request.ExternalTrigger?.IsEnabled == true &&
            request.Schedule.Kind == TaskScheduleKind.OneShot &&
            request.Schedule.DeleteAfterRun)
        {
            return (null, "External webhook triggers cannot be combined with delete-after-run one-shot schedules.");
        }

        if (request.ToolApprovalRunMode == ToolApprovalRunMode.UseAgentPolicy)
        {
            return (null, "Background tasks require a concrete approval mode (auto-approve or auto-reject).");
        }

        if (request.ToolApprovalRunMode is not ToolApprovalRunMode.AutoRejectRequired and
            not ToolApprovalRunMode.AutoApproveRequired)
        {
            return (null, "The tool approval run mode is invalid.");
        }

        if (request.KeepLastRuns is < 1)
        {
            return (null, "Keep last runs must be 1 or more, or empty to keep every run.");
        }

        var requiresTimeZone = request.Schedule.Kind is TaskScheduleKind.OneShot or TaskScheduleKind.Cron;
        var effectiveTimeZoneId = requiresTimeZone && string.IsNullOrWhiteSpace(request.Schedule.TimeZoneId)
            ? AssistantUiSettings.DefaultTimeZoneId
            : request.Schedule.TimeZoneId;
        var normalizedTimeZoneId = TimeZoneResolver.NormalizeOrEmpty(effectiveTimeZoneId);
        var predecessorTaskId = request.Schedule.Kind == TaskScheduleKind.AfterTask
            ? request.Schedule.PredecessorTaskId?.Trim()
            : null;
        var modelOverrideId = string.IsNullOrWhiteSpace(request.ModelOverrideId)
            ? null
            : request.ModelOverrideId.Trim();

        if (requiresTimeZone && string.IsNullOrWhiteSpace(normalizedTimeZoneId))
        {
            return (null, "The selected time zone is invalid.");
        }

        if (modelOverrideId is not null && !ChatModelProviderSelection.ContainsProvider(modelProviderSettings, modelOverrideId))
        {
            return (null, "The selected model provider is no longer available for chat.");
        }

        if (scheduleValidation.CronError is not null)
        {
            return (null, scheduleValidation.CronError);
        }

        if (request.Schedule.Kind == TaskScheduleKind.AfterTask)
        {
            if (scheduleValidation.PredecessorError is not null)
            {
                return (null, scheduleValidation.PredecessorError);
            }

            if (string.Equals(predecessorTaskId, taskId, StringComparison.Ordinal))
            {
                return (null, "A task cannot run after itself.");
            }

            if (!existingTasks.Any(task => string.Equals(task.Id, predecessorTaskId, StringComparison.Ordinal)))
            {
                return (null, "The predecessor task could not be found.");
            }

            if (WouldCreateAfterTaskCycle(taskId, predecessorTaskId!, existingTasks))
            {
                return (null, "This after-task schedule would create a task chain cycle.");
            }
        }

        return (new AgentTaskDefinition
        {
            Id = taskId,
            UserId = userId,
            Name = request.Name.Trim(),
            Instruction = request.InstructionSource is null ? request.Instruction.Trim() : request.Instruction,
            InstructionSource = request.InstructionSource,
            InstructionMode = request.InstructionMode,
            AgentId = request.AgentId,
            ModelOverrideId = modelOverrideId,
            IsEnabled = request.IsEnabled,
            ToolApprovalRunMode = request.ToolApprovalRunMode,
            RunScope = runScope,
            ExternalTrigger = request.ExternalTrigger ?? new TaskExternalTrigger(),
            NotifyOnCompletion = request.NotifyOnCompletion,
            WaitForOwnerProviderIdle = request.WaitForOwnerProviderIdle,
            SessionMode = request.SessionMode,
            MaxTokenBudget = request.MaxTokenBudget,
            KeepLastRuns = request.KeepLastRuns,
            CreatedAt = request.CreatedAt,
            Schedule = new TaskSchedule
            {
                Kind = request.Schedule.Kind,
                IntervalMinutes = request.Schedule.Kind == TaskScheduleKind.Interval ? request.Schedule.IntervalMinutes : null,
                RunAt = request.Schedule.Kind == TaskScheduleKind.OneShot ? request.Schedule.RunAt : null,
                CronExpression = request.Schedule.Kind == TaskScheduleKind.Cron ? request.Schedule.CronExpression?.Trim() : null,
                TimeZoneId = requiresTimeZone ? normalizedTimeZoneId : string.Empty,
                DeleteAfterRun = request.Schedule.Kind == TaskScheduleKind.OneShot && request.Schedule.DeleteAfterRun,
                PredecessorTaskId = predecessorTaskId
            }
        }, null);
    }

    internal static string? ValidatePersistedSchedule(AgentTaskDefinitionFile file)
    {
        if (file.Schedule is null)
        {
            return "A task schedule is required.";
        }

        if (!TryParseScheduleKind(file.Schedule.Kind, out var kind))
        {
            return "The task schedule kind is invalid.";
        }

        var validation = ValidateSchedule(
            kind,
            file.Schedule.IntervalMinutes,
            !string.IsNullOrWhiteSpace(file.Schedule.RunAt),
            file.Schedule.Cron,
            file.Schedule.PredecessorTaskId);
        return validation.BasicError ?? validation.CronError ?? validation.PredecessorError;
    }

    private static (string? BasicError, string? CronError, string? PredecessorError) ValidateSchedule(
        TaskScheduleKind kind,
        int? intervalMinutes,
        bool hasRunAt,
        string? cronExpression,
        string? predecessorTaskId)
    {
        if (!Enum.IsDefined(kind))
        {
            return ("The task schedule kind is invalid.", null, null);
        }

        if (kind == TaskScheduleKind.Interval && intervalMinutes is null or < 1)
        {
            return ("Interval schedules require at least one minute.", null, null);
        }

        if (kind == TaskScheduleKind.OneShot && !hasRunAt)
        {
            return ("A one-shot schedule requires a run time.", null, null);
        }

        string? cronError = null;
        if (kind == TaskScheduleKind.Cron)
        {
            if (string.IsNullOrWhiteSpace(cronExpression))
            {
                cronError = "Cron expression is required.";
            }
            else
            {
                try
                {
                    _ = CronExpression.Parse(cronExpression.Trim());
                }
                catch (CronFormatException ex)
                {
                    cronError = $"Invalid cron expression: {ex.Message}";
                }
            }
        }

        var predecessorError = kind == TaskScheduleKind.AfterTask && string.IsNullOrWhiteSpace(predecessorTaskId)
            ? "An after-task schedule requires a predecessor task."
            : null;
        return (null, cronError, predecessorError);
    }

    private static bool TryParseScheduleKind(string value, out TaskScheduleKind kind)
    {
        foreach (var candidate in Enum.GetValues<TaskScheduleKind>())
        {
            if (string.Equals(candidate.ToString(), value, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    TaskFileValueFormatter.ToSnakeCase(candidate.ToString()),
                    value?.Trim().Replace('-', '_'),
                    StringComparison.OrdinalIgnoreCase))
            {
                kind = candidate;
                return true;
            }
        }

        kind = default;
        return false;
    }

    private async Task<(AgentTaskDefinition? Task, string? Error)> TrySaveTaskAsync(
        AgentTaskDefinition task,
        AgentTaskDefinition? expected,
        CancellationToken ct, string? creationDirectoryName, bool preserveEnabled)
    {
        try
        {
            AgentTaskDefinition? authoritativeTask = null;
            string? validationError = null;
            var saved = await taskScheduler.RunTaskSaveAsync(
                userId,
                task.Id,
                async saveCt =>
                {
                    var current = await taskRepository
                        .GetTaskAsync(userId, task.Id, saveCt)
                        .ConfigureAwait(false);
                    // Creation must not replace an existing task's provisioned identity.
                    if (expected is null ? current is not null :
                        current is null || !expected.HasSameContentAs(current))
                    {
                        return null;
                    }

                    // Another task's predecessor may have changed while this save waited.
                    if (task.Schedule.Kind == TaskScheduleKind.AfterTask)
                    {
                        var currentTasks = await taskRepository.GetTasksByUserAsync(userId, saveCt).ConfigureAwait(false);
                        (_, validationError) = ValidateAndSanitizeTask(task, task.Id, currentTasks, task.RunScope);
                        if (validationError is not null) { return null; }
                    }

                    // Reject invalid webhook changes before removing the old key grants.
                    taskRepository.ValidateTask(task);

                    // Preserve an untouched Enabled switch under the same lock as one-shot finalization.
                    if (preserveEnabled && current is not null) { task.IsEnabled = current.IsEnabled; }

                    var previousTriggerSlug = current?.ExternalTrigger.IsEnabled == true
                        ? current.ExternalTrigger.Slug
                        : null;
                    if (!string.IsNullOrWhiteSpace(previousTriggerSlug) &&
                        (!task.ExternalTrigger.IsEnabled ||
                         !string.Equals(previousTriggerSlug, task.ExternalTrigger.Slug, StringComparison.OrdinalIgnoreCase)))
                    {
                        await referenceCleanupService
                            .ClearTaskReferencesAsync(previousTriggerSlug, saveCt)
                            .ConfigureAwait(false);
                    }

                    await taskRepository.SaveTaskAsync(task, saveCt, creationDirectoryName).ConfigureAwait(false);
                    authoritativeTask = await taskRepository
                        .GetTaskAsync(userId, task.Id, saveCt)
                        .ConfigureAwait(false);
                    return authoritativeTask;
                },
                ct).ConfigureAwait(false);
            return saved
                ? (authoritativeTask, null)
                : (null, validationError ?? "The task changed or no longer exists. Reload and try again.");
        }
        catch (DuplicateExternalTriggerSlugException ex)
        {
            return (null, ex.Message);
        }
    }

    private (TaskRunScope? Scope, string? Error) ValidateRunScope(TaskRunScope? scope, string agentId, string taskId)
    {
        if (scope is null) { return (null, null); }

        if (scope.AllowedDirectories.Count != 0)
        {
            return (null, "Task external folders are unavailable. External folder access belongs to Projects.");
        }

        var agent = appService.ResolveAgentDefinition(agentId);
        if (!string.Equals(agent.Id, agentId, StringComparison.Ordinal))
        {
            return (null, "The selected agent is no longer available.");
        }

        var catalog = TaskRunScopeValidationCatalogFactory.Create(appService, crewService, userId, userDirectoryService);
        var sanitizedScope = scope.Clone();
        var sanitization = ToolReferenceSanitizer.RemoveUnavailableTools(
            sanitizedScope.AllowedToolNames, sanitizedScope.PinnedToolNames, sanitizedScope.ToolPolicies,
            catalog.ToolNames, sanitizedScope.AgentToolIds, catalog.ExposedAgentIds);
        LogToolReferenceSanitization(taskId, sanitization);
        if (!taskRunScopeResolver.TryResolve(sanitizedScope, agent,
            effectiveAgent => crewService.ResolveAllowedDirectories(userId, effectiveAgent),
            out var surface, out var error, catalog))
        {
            return (null, error ?? "The task run scope is invalid.");
        }

        return (surface!.NormalizedRunScope, null);
    }

    private void LogToolReferenceSanitization(
        string taskId,
        ToolReferenceSanitizationResult result)
    {
        if (!result.HasChanges)
        {
            return;
        }

        logger.LogWarning(
            "Removed stale tool references from task {TaskId} run scope for user {UserId}. Removed allowed tools: {RemovedAllowedToolCount}; removed pinned tools: {RemovedPinnedToolCount}; removed tool policies: {RemovedPolicyToolCount}; removed agent tools: {RemovedAgentToolIds}.",
            taskId,
            userId,
            result.RemovedAllowedToolNames.Count,
            result.RemovedPinnedToolNames.Count,
            result.RemovedPolicyToolNames.Count,
            result.RemovedAgentToolIds);
    }

    private static bool WouldCreateAfterTaskCycle(
        string taskId,
        string predecessorTaskId,
        IReadOnlyList<AgentTaskDefinition> existingTasks)
    {
        var tasksById = existingTasks
            .Where(task => !string.Equals(task.Id, taskId, StringComparison.Ordinal))
            .ToDictionary(task => task.Id, StringComparer.Ordinal);
        var visitedTaskIds = new HashSet<string>(StringComparer.Ordinal);
        var cursorId = predecessorTaskId;

        while (visitedTaskIds.Add(cursorId))
        {
            if (string.Equals(cursorId, taskId, StringComparison.Ordinal))
            {
                return true;
            }

            if (!tasksById.TryGetValue(cursorId, out var cursor) ||
                cursor.Schedule.Kind != TaskScheduleKind.AfterTask ||
                string.IsNullOrWhiteSpace(cursor.Schedule.PredecessorTaskId))
            {
                return false;
            }

            cursorId = cursor.Schedule.PredecessorTaskId.Trim();
        }

        return false;
    }
}
