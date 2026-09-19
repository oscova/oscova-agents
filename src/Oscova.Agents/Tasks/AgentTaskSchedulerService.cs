using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Notifications;

namespace Oscova.Agents.Tasks;

/// <summary>
/// Background scheduler that starts enabled agent tasks when their schedules become due.
/// </summary>
/// <param name="serviceProvider">The root service provider used to create scoped task services.</param>
/// <param name="taskStatusNotifications">The notification hub used to publish task status updates.</param>
/// <param name="logger">The logger used for scheduler activity and failures.</param>
public class AgentTaskSchedulerService(
    IServiceProvider serviceProvider,
    NotificationHub<TaskStatusUpdate> taskStatusNotifications,
    ILogger<AgentTaskSchedulerService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    // Owner identity remains part of admission even when a deleted task ID is reused.
    private readonly ConcurrentDictionary<TaskOwnerKey, AgentTaskRun> _runningTasks = new();

    // Holds the most recent run per task (running or completed). Never removed on completion
    // so LiveMessages stays accessible after the run ends.
    private readonly ConcurrentDictionary<TaskOwnerKey, AgentTaskRun> _lastRunPerTask = new();

    // Serializes admission with shutdown so every accepted execution is included in the drain.
    private readonly Lock _executionLifetimeGate = new();
    private readonly SemaphoreSlim _taskDefinitionMutationGate = new(1, 1);
    private readonly ConcurrentDictionary<TaskOwnerKey, SemaphoreSlim> _taskMutationGates = [];
    private readonly CancellationTokenSource _executionLifetimeCancellation = new();
    private readonly Dictionary<TaskOwnerKey, OwnedTaskExecution> _ownedExecutions = [];
    private readonly HashSet<TaskOwnerKey> _closedTaskAdmissions = [];
    private readonly Dictionary<TaskOwnerKey, byte[]> _taskDefinitionFingerprints = [];
    private bool _isStopping;
    private int _disposed;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogDebug("Agent task scheduler started. Tick interval: {Interval}s", TickInterval.TotalSeconds);

        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await TickAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Unhandled error during scheduler tick.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        logger.LogInformation("Agent task scheduler stopped.");
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Task[] ownedExecutions;
        lock (_executionLifetimeGate)
        {
            _isStopping = true;
            ownedExecutions = [.. _ownedExecutions.Values.Select(execution => execution.Task)];
        }

        _executionLifetimeCancellation.Cancel();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(ownedExecutions)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _executionLifetimeCancellation.Cancel();
        _executionLifetimeCancellation.Dispose();
        _taskDefinitionMutationGate.Dispose();
        foreach (var gate in _taskMutationGates.Values)
        {
            gate.Dispose();
        }
        base.Dispose();
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        var scope = serviceProvider.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var repository = scope.ServiceProvider.GetRequiredService<IAgentTaskRepository>();

            var enabledTasks = await repository.GetAllEnabledTasksAsync(ct).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;

            foreach (var task in enabledTasks)
            {
                try
                {
                    var latestRun = _runningTasks.GetValueOrDefault(new TaskOwnerKey(task.UserId, task.Id))
                                    ?? await repository.GetLatestRunMetadataAsync(task.UserId, task.Id, ct).ConfigureAwait(false);

                    if (!TaskScheduleEvaluator.IsDue(task, latestRun, now))
                    {
                        continue;
                    }

                    QueueTaskExecution(task, now, TaskTriggerSource.Schedule, new Dictionary<string, JsonElement>());
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error evaluating/starting task '{TaskName}' (ID: {TaskId})", task.Name, task.Id);
                }
            }
        }
    }

    /// <summary>
    /// Queues an enabled task for immediate manual execution if it is not already running.
    /// </summary>
    /// <param name="task">The task definition to run.</param>
    /// <param name="source">Identifies how the task was triggered.</param>
    /// <param name="parameters">Validated external trigger parameters to capture with the run.</param>
    /// <param name="ct">A token for cancelling pre-queue trigger work. Once queued, manual/webhook execution is owned by the scheduler.</param>
    /// <returns>The trigger outcome, including the new or already-running run identifier.</returns>
    public Task<TaskTriggerOutcome> TriggerTaskAsync(
        AgentTaskDefinition task,
        TaskTriggerSource source = TaskTriggerSource.Manual,
        IReadOnlyDictionary<string, JsonElement>? parameters = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!task.IsEnabled)
        {
            return Task.FromResult(new TaskTriggerOutcome
            {
                Started = false,
                Status = AgentTaskStatus.Pending,
                Message = $"Task \"{task.Name}\" is paused. Resume it before running it."
            });
        }

        var outcome = QueueTaskExecution(
            task,
            DateTimeOffset.UtcNow,
            source,
            parameters ?? new Dictionary<string, JsonElement>());
        return Task.FromResult(outcome);
    }

    private async Task ExecuteTaskAsync(AgentTaskDefinition task, AgentTaskRun queuedRun, CancellationToken ct)
    {
        var taskKey = new TaskOwnerKey(task.UserId, task.Id);
        AgentTaskRun run;
        // Once execution returns, later persistence or one-shot bookkeeping must not rewrite its outcome.
        var executorReturned = false;
        var statusNotificationSent = false;

        try
        {
            var scope = serviceProvider.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var repository = scope.ServiceProvider.GetRequiredService<IAgentTaskRepository>();
                var executor = scope.ServiceProvider.GetRequiredService<IAgentTaskExecutor>();

                run = await executor.ExecuteAsync(task, queuedRun, ct).ConfigureAwait(false);
                executorReturned = true;

                // Ensure timestamps are always set, regardless of executor implementation
                run.StartedAt ??= DateTimeOffset.UtcNow;
                if (run.Status is AgentTaskStatus.Completed or AgentTaskStatus.Failed)
                {
                    run.CompletedAt ??= DateTimeOffset.UtcNow;
                }

                _runningTasks[taskKey] = run;

                await repository.SaveRunAsync(run, ct).ConfigureAwait(false);
                await taskStatusNotifications.NotifyAsync(
                    task.UserId,
                    new TaskStatusUpdate(task.Id, task.Name, run.Status, StatusMessageFor(run))).ConfigureAwait(false);
                statusNotificationSent = true;

                logger.LogDebug(
                    "Task '{TaskName}' (ID: {TaskId}) finished with status {Status}",
                    task.Name,
                    task.Id,
                    run.Status);

                // Failed one-shots stay enabled for manual retry; successful runs retain their finalization policy.
                if (task.Schedule.Kind == TaskScheduleKind.OneShot && run.Status == AgentTaskStatus.Completed)
                {
                    await RunTaskFinalizationAsync(
                        taskKey,
                        async finalizeCt =>
                        {
                            var currentTask = await repository
                                .GetTaskAsync(task.UserId, task.Id, finalizeCt)
                                .ConfigureAwait(false);
                            var currentSchedule = currentTask?.Schedule;
                            // Finalize only the one-shot schedule that produced this run; a later schedule edit owns future behavior.
                            if (currentTask is null ||
                                currentSchedule?.Kind != TaskScheduleKind.OneShot ||
                                currentSchedule.RunAt != task.Schedule.RunAt ||
                                currentSchedule.DeleteAfterRun != task.Schedule.DeleteAfterRun)
                            {
                                return;
                            }

                            if (currentSchedule.DeleteAfterRun)
                            {
                                CloseTaskAdmission(taskKey);
                                try
                                {
                                    await repository
                                        .DeleteTaskAsync(task.UserId, task.Id, finalizeCt)
                                        .ConfigureAwait(false);
                                    _lastRunPerTask.TryRemove(taskKey, out _);
                                }
                                catch
                                {
                                    ReopenTaskAdmission(taskKey);
                                    throw;
                                }
                            }
                            else
                            {
                                currentTask.IsEnabled = false;
                                await repository.SaveTaskAsync(currentTask, finalizeCt).ConfigureAwait(false);
                                var savedTask = await repository
                                    .GetTaskAsync(task.UserId, task.Id, finalizeCt)
                                    .ConfigureAwait(false);
                                if (savedTask is not null)
                                {
                                    SetTaskDefinitionFingerprint(taskKey, savedTask);
                                }
                            }
                        },
                        ct).ConfigureAwait(false);
                }

                if (run.Status == AgentTaskStatus.Completed && !IsTaskAdmissionClosed(taskKey))
                {
                    await TriggerDependentTasksAsync(repository, task, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (executorReturned)
            {
                logger.LogWarning(
                    "Task scheduler stopped while finalizing task '{TaskName}' (ID: {TaskId}); preserving its returned status.",
                    task.Name,
                    task.Id);
                await PreserveReturnedRunAsync(task, statusNotificationSent).ConfigureAwait(false);
                return;
            }

            logger.LogDebug("Task '{TaskName}' (ID: {TaskId}) was cancelled.", task.Name, task.Id);

            if (_runningTasks.TryGetValue(taskKey, out var cancelledRun))
            {
                cancelledRun.Status = AgentTaskStatus.Failed;
                cancelledRun.CompletedAt = DateTimeOffset.UtcNow;
                cancelledRun.Error = "Task was cancelled.";
                await PersistRunAsync(cancelledRun).ConfigureAwait(false);
                await taskStatusNotifications.NotifyAsync(
                    task.UserId,
                    new TaskStatusUpdate(task.Id, task.Name, cancelledRun.Status, StatusMessageFor(cancelledRun))).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            if (executorReturned)
            {
                logger.LogError(
                    ex,
                    "Task '{TaskName}' (ID: {TaskId}) returned a terminal status, but post-execution bookkeeping failed.",
                    task.Name,
                    task.Id);
                await PreserveReturnedRunAsync(task, statusNotificationSent).ConfigureAwait(false);
                return;
            }

            logger.LogError(ex, "Error executing task '{TaskName}' (ID: {TaskId})", task.Name, task.Id);

            if (_runningTasks.TryGetValue(taskKey, out var failedRun))
            {
                failedRun.Status = AgentTaskStatus.Failed;
                failedRun.CompletedAt = DateTimeOffset.UtcNow;
                failedRun.Error = ex.Message;
                await PersistRunAsync(failedRun).ConfigureAwait(false);
                await taskStatusNotifications.NotifyAsync(
                    task.UserId,
                    new TaskStatusUpdate(task.Id, task.Name, failedRun.Status, StatusMessageFor(failedRun))).ConfigureAwait(false);
            }
        }
        finally
        {
            _runningTasks.TryRemove(taskKey, out _);
        }
    }

    /// <summary>Retries durable status publication without changing the executor-owned run outcome.</summary>
    private async Task PreserveReturnedRunAsync(AgentTaskDefinition task, bool statusNotificationSent)
    {
        if (!_runningTasks.TryGetValue(new TaskOwnerKey(task.UserId, task.Id), out var returnedRun))
        {
            return;
        }

        await PersistRunAsync(returnedRun).ConfigureAwait(false);
        if (!statusNotificationSent)
        {
            await taskStatusNotifications.NotifyAsync(
                task.UserId,
                new TaskStatusUpdate(task.Id, task.Name, returnedRun.Status, StatusMessageFor(returnedRun))).ConfigureAwait(false);
        }
    }

    // Queues tasks configured to run after the given task completes successfully (same owner only).
    // Never throws: a failure here must not flip the just-completed predecessor run to Failed.
    private async Task TriggerDependentTasksAsync(
        IAgentTaskRepository repository,
        AgentTaskDefinition completedTask,
        CancellationToken ct)
    {
        try
        {
            var enabledTasks = await repository.GetAllEnabledTasksAsync(ct).ConfigureAwait(false);
            var afterTasksById = enabledTasks
                .Where(task => string.Equals(task.UserId, completedTask.UserId, StringComparison.OrdinalIgnoreCase) &&
                               task.Schedule.Kind == TaskScheduleKind.AfterTask)
                .ToDictionary(task => task.Id, StringComparer.Ordinal);

            foreach (var candidate in afterTasksById.Values)
            {
                if (!string.Equals(candidate.Schedule.PredecessorTaskId, completedTask.Id, StringComparison.Ordinal))
                {
                    continue;
                }

                if (WouldCreateAfterTaskCycle(afterTasksById, completedTask, candidate))
                {
                    continue;
                }

                QueueTaskExecution(candidate, DateTimeOffset.UtcNow, TaskTriggerSource.AfterTask, new Dictionary<string, JsonElement>());
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to queue dependent tasks after task '{TaskId}' completed.", completedTask.Id);
        }
    }

    private static bool WouldCreateAfterTaskCycle(
        IReadOnlyDictionary<string, AgentTaskDefinition> afterTasksById,
        AgentTaskDefinition completedTask,
        AgentTaskDefinition candidate)
    {
        var cursorId = completedTask.Schedule.Kind == TaskScheduleKind.AfterTask
            ? completedTask.Schedule.PredecessorTaskId
            : null;
        var visitedTaskIds = new HashSet<string>(StringComparer.Ordinal);

        while (!string.IsNullOrWhiteSpace(cursorId) && visitedTaskIds.Add(cursorId))
        {
            if (string.Equals(cursorId, candidate.Id, StringComparison.Ordinal))
            {
                return true;
            }

            if (!afterTasksById.TryGetValue(cursorId, out var cursor) ||
                cursor.Schedule.Kind != TaskScheduleKind.AfterTask)
            {
                return false;
            }

            cursorId = cursor.Schedule.PredecessorTaskId;
        }

        return false;
    }

    /// <summary>
    /// Gets currently running task runs.
    /// </summary>
    /// <param name="userId">The owner whose runs to return.</param>
    /// <returns>A snapshot of running task runs.</returns>
    public IEnumerable<AgentTaskRun> GetRunningTasks(string userId)
    {
        return _runningTasks
            .Where(pair => string.Equals(pair.Key.UserId, userId, StringComparison.OrdinalIgnoreCase) &&
                           pair.Value.Status == AgentTaskStatus.Running)
            .Select(pair => pair.Value);
    }

    /// <summary>
    /// Returns the most recent run for a task, whether currently running or completed,
    /// as long as it has not been replaced by a newer run. Returns false if no run has
    /// started since the last app restart.
    /// </summary>
    /// <param name="userId">The task owner's user identifier.</param>
    /// <param name="taskId">The task identifier to check.</param>
    /// <param name="run">The latest owner-bound run when found.</param>
    public bool TryGetLastRun(string userId, string taskId, out AgentTaskRun? run)
        => _lastRunPerTask.TryGetValue(new TaskOwnerKey(userId, taskId), out run);

    /// <summary>
    /// Returns whether a task currently has an active running entry.
    /// </summary>
    /// <param name="userId">The task owner's user identifier.</param>
    /// <param name="taskId">The task identifier to check.</param>
    /// <returns><see langword="true"/> when the task is currently running.</returns>
    public bool IsTaskRunning(string userId, string taskId)
        => _runningTasks.TryGetValue(new TaskOwnerKey(userId, taskId), out var run) && run.Status == AgentTaskStatus.Running;

    /// <summary>Cancels a task and keeps its admission closed while its definition is deleted.</summary>
    /// <param name="userId">The task owner's user identifier.</param>
    /// <param name="taskId">The task identifier being deleted.</param>
    /// <param name="deleteAsync">Deletes the persisted task definition after execution drains and reports whether it existed.</param>
    /// <param name="ct">A token that bounds cancellation, draining, and deletion.</param>
    /// <param name="afterDeleteAsync">Completes deletion side effects before task saves may resume.</param>
    public async Task RunTaskDeletionAsync(
        string userId,
        string taskId,
        Func<CancellationToken, Task<bool>> deleteAsync,
        CancellationToken ct = default,
        Func<CancellationToken, Task>? afterDeleteAsync = null)
    {
        var taskKey = new TaskOwnerKey(userId, taskId);
        var taskGate = GetTaskMutationGate(taskKey);
        await taskGate.WaitAsync(ct).ConfigureAwait(false);
        var deleted = false;
        try
        {
            OwnedTaskExecution? ownedExecution;
            lock (_executionLifetimeGate)
            {
                _closedTaskAdmissions.Add(taskKey);
                _ownedExecutions.TryGetValue(taskKey, out ownedExecution);
            }

            try
            {
                await CancelOwnedExecutionAsync(ownedExecution, ct).ConfigureAwait(false);
                await _taskDefinitionMutationGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    deleted = await deleteAsync(ct).ConfigureAwait(false);
                    if (deleted && afterDeleteAsync is not null)
                    {
                        await afterDeleteAsync(ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _taskDefinitionMutationGate.Release();
                }
            }
            finally
            {
                if (deleted)
                {
                    // A deleted definition cannot own a cached last run because a later task may reuse its ID.
                    _lastRunPerTask.TryRemove(taskKey, out _);
                }
                else
                {
                    ReopenTaskAdmission(taskKey);
                }
            }
        }
        finally
        {
            taskGate.Release();
        }
    }

    /// <summary>Persists a task definition and reopens admission only when the callback saved it.</summary>
    public async Task<bool> RunTaskSaveAsync(
        string userId,
        string taskId,
        Func<CancellationToken, Task<AgentTaskDefinition?>> saveAsync,
        CancellationToken ct = default)
    {
        var taskKey = new TaskOwnerKey(userId, taskId);
        var taskGate = GetTaskMutationGate(taskKey);
        await taskGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _taskDefinitionMutationGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var savedTask = await saveAsync(ct).ConfigureAwait(false);
                if (savedTask is not null)
                {
                    ReopenTaskAdmission(taskKey, savedTask);
                }

                return savedTask is not null;
            }
            finally
            {
                _taskDefinitionMutationGate.Release();
            }
        }
        finally
        {
            taskGate.Release();
        }
    }

    private async Task RunTaskFinalizationAsync(
        TaskOwnerKey taskKey,
        Func<CancellationToken, Task> finalizeAsync,
        CancellationToken ct)
    {
        if (IsTaskAdmissionClosed(taskKey))
        {
            return;
        }

        var taskGate = GetTaskMutationGate(taskKey);
        await taskGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsTaskAdmissionClosed(taskKey))
            {
                return;
            }

            await _taskDefinitionMutationGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await finalizeAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _taskDefinitionMutationGate.Release();
            }
        }
        finally
        {
            taskGate.Release();
        }
    }

    private SemaphoreSlim GetTaskMutationGate(TaskOwnerKey taskKey)
        => _taskMutationGates.GetOrAdd(taskKey, static _ => new SemaphoreSlim(1, 1));

    private static async Task CancelOwnedExecutionAsync(
        OwnedTaskExecution? ownedExecution,
        CancellationToken ct)
    {
        if (ownedExecution?.Task.IsCompleted != false)
        {
            return;
        }

        try
        {
            ownedExecution.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        await ownedExecution.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private TaskTriggerOutcome QueueTaskExecution(
        AgentTaskDefinition task,
        DateTimeOffset startedAt,
        TaskTriggerSource source,
        IReadOnlyDictionary<string, JsonElement> parameters)
    {
        lock (_executionLifetimeGate)
        {
            var taskKey = new TaskOwnerKey(task.UserId, task.Id);
            if (_isStopping)
            {
                return new TaskTriggerOutcome
                {
                    Started = false,
                    Status = AgentTaskStatus.Pending,
                    Message = "The task scheduler is stopping."
                };
            }

            if (_closedTaskAdmissions.Contains(taskKey))
            {
                return new TaskTriggerOutcome
                {
                    Started = false,
                    Status = AgentTaskStatus.Pending,
                    Message = "The task is being deleted or no longer exists."
                };
            }

            if (_taskDefinitionFingerprints.TryGetValue(taskKey, out var currentFingerprint) &&
                !currentFingerprint.AsSpan().SequenceEqual(task.GetContentFingerprint()))
            {
                return new TaskTriggerOutcome
                {
                    Started = false,
                    Status = AgentTaskStatus.Pending,
                    Message = "The task changed or no longer exists. Reload it before running."
                };
            }

            while (true)
            {
                if (_runningTasks.TryGetValue(taskKey, out var existingRun))
                {
                    return new TaskTriggerOutcome
                    {
                        Started = false,
                        RunId = existingRun.Id,
                        Status = existingRun.Status,
                        Message = "That task is already running."
                    };
                }

                var queuedRun = new AgentTaskRun
                {
                    TaskId = task.Id,
                    UserId = task.UserId,
                    StartedAt = startedAt,
                    Status = AgentTaskStatus.Running,
                    TriggerSource = source,
                    TriggerParameters = parameters.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value.Clone(),
                        StringComparer.OrdinalIgnoreCase),
                    LiveMessages = [],
                };

                if (!_runningTasks.TryAdd(taskKey, queuedRun))
                {
                    continue;
                }

                _lastRunPerTask[taskKey] = queuedRun;
                Task startNotification = taskStatusNotifications.NotifyAsync(task.UserId, new TaskStatusUpdate(
                    task.Id,
                    task.Name,
                    queuedRun.Status,
                    GetStartMessage(source)));
                _ = startNotification.ContinueWith(
                    t => logger.LogWarning(t.Exception, "Failed to send task status notification for task '{TaskId}'.", task.Id),
                    TaskContinuationOptions.OnlyOnFaulted);

                logger.LogDebug("Starting task '{TaskName}' (ID: {TaskId}) via {Source}", task.Name, task.Id, source);

                var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    _executionLifetimeCancellation.Token);
                var execution = ExecuteTaskAfterStartNotificationAsync(
                    task,
                    queuedRun,
                    startNotification,
                    executionCancellation.Token);
                var ownedExecution = new OwnedTaskExecution(executionCancellation, execution);
                _ownedExecutions[taskKey] = ownedExecution;
                _ = execution.ContinueWith(
                    _ =>
                    {
                        lock (_executionLifetimeGate)
                        {
                            if (_ownedExecutions.TryGetValue(taskKey, out var current) &&
                                ReferenceEquals(current, ownedExecution))
                            {
                                _ownedExecutions.Remove(taskKey);
                            }
                        }

                        executionCancellation.Dispose();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                return new TaskTriggerOutcome
                {
                    Started = true,
                    RunId = queuedRun.Id,
                    Status = queuedRun.Status,
                    Message = $"Started \"{task.Name}\"."
                };
            }
        }
    }

    private async Task ExecuteTaskAfterStartNotificationAsync(
        AgentTaskDefinition task,
        AgentTaskRun queuedRun,
        Task startNotification,
        CancellationToken cancellationToken)
    {
        try
        {
            await startNotification.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await ExecuteTaskAsync(task, queuedRun, cancellationToken).ConfigureAwait(false);
    }

    private static string GetStartMessage(TaskTriggerSource source)
    {
        return source switch
        {
            TaskTriggerSource.Manual => "Task started manually.",
            TaskTriggerSource.ExternalWebhook => "Task started by webhook.",
            TaskTriggerSource.AfterTask => "Task started by a preceding task.",
            _ => "Task started on schedule."
        };
    }

    private static string? StatusMessageFor(AgentTaskRun run) =>
        run.IsEncrypted ? null : run.Error;

    private async Task PersistRunAsync(AgentTaskRun run)
    {
        try
        {
            var scope = serviceProvider.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var repository = scope.ServiceProvider.GetRequiredService<IAgentTaskRepository>();
                await repository.SaveRunAsync(run, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist run record for task '{TaskId}'.", run.TaskId);
        }
    }

    private void CloseTaskAdmission(TaskOwnerKey taskKey)
    {
        lock (_executionLifetimeGate)
        {
            _closedTaskAdmissions.Add(taskKey);
        }
    }

    private void ReopenTaskAdmission(
        TaskOwnerKey taskKey,
        AgentTaskDefinition? task = null)
    {
        lock (_executionLifetimeGate)
        {
            _closedTaskAdmissions.Remove(taskKey);
            if (task is not null)
            {
                _taskDefinitionFingerprints[taskKey] = task.GetContentFingerprint();
            }
        }
    }

    private void SetTaskDefinitionFingerprint(
        TaskOwnerKey taskKey,
        AgentTaskDefinition task)
    {
        lock (_executionLifetimeGate)
        {
            _taskDefinitionFingerprints[taskKey] = task.GetContentFingerprint();
        }
    }

    private bool IsTaskAdmissionClosed(TaskOwnerKey taskKey)
    {
        lock (_executionLifetimeGate)
        {
            return _closedTaskAdmissions.Contains(taskKey);
        }
    }

    private sealed record OwnedTaskExecution(CancellationTokenSource Cancellation, Task Task);

    private readonly record struct TaskOwnerKey
    {
        public TaskOwnerKey(string userId, string taskId)
        {
            UserId = userId.ToUpperInvariant();
            TaskId = taskId.ToUpperInvariant();
        }

        public string UserId { get; }

        public string TaskId { get; }
    }
}
