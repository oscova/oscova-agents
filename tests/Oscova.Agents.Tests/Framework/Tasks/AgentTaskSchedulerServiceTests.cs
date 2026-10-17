using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Tests.Framework.Logging;
using Oscova.Agents.Notifications;
using Oscova.Agents.Tasks;
using System.Collections.Concurrent;

namespace Oscova.Agents.Tests.Framework.Tasks;

public class AgentTaskSchedulerServiceTests
{
    /// <summary>User cleanup drains that owner's executions and permits retry after releasing admission.</summary>
    [Fact]
    public async Task CancelAndAcquireUserAsync_DrainsOnlyOwnerAndBlocksStartsUntilReleased()
    {
        using var context = new SchedulerTestContext();
        var target = SchedulerTestContext.CreateTask();
        var other = SchedulerTestContext.CreateTask(userId: "user-2");
        await context.Scheduler.TriggerTaskAsync(target);
        await context.Scheduler.TriggerTaskAsync(other);
        await WaitForExecutionCountAsync(context.Executor, other.Id, 1);

        using (await context.Scheduler.CancelAndAcquireUserAsync(target.UserId).WaitAsync(TimeSpan.FromSeconds(2)))
        {
            Assert.False(context.Scheduler.IsTaskRunning(target.UserId, target.Id));
            Assert.True(context.Scheduler.IsTaskRunning(other.UserId, other.Id));
            var blocked = await context.Scheduler.TriggerTaskAsync(target);
            Assert.False(blocked.Started);
        }
        var retry = await context.Scheduler.TriggerTaskAsync(target);
        Assert.True(retry.Started);
        context.Executor.Release();
        await context.Scheduler.StopAsync(CancellationToken.None);
    }

    /// <summary>Cancellation before execution clears the running entry so the task can start again.</summary>
    [Fact]
    public async Task CancelAndAcquireUserAsync_WhenStartNotificationIsBlocked_ClearsPendingRun()
    {
        using var context = new SchedulerTestContext();
        var task = SchedulerTestContext.CreateTask();
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = context.TaskStatusNotifications.Subscribe(task.UserId, async update =>
        {
            if (update.Status == AgentTaskStatus.Running)
            {
                notified.TrySetResult();
                await release.Task;
            }
        });
        try
        {
            await context.Scheduler.TriggerTaskAsync(task);
            await notified.Task.WaitAsync(TimeSpan.FromSeconds(2));
            using (await context.Scheduler.CancelAndAcquireUserAsync(task.UserId).WaitAsync(TimeSpan.FromSeconds(2)))
            {
                Assert.False(context.Scheduler.IsTaskRunning(task.UserId, task.Id));
                Assert.False(context.Executor.ExecutionStarted.Task.IsCompleted);
            }
            release.TrySetResult();
            Assert.True((await context.Scheduler.TriggerTaskAsync(task)).Started);
            context.Executor.Release();
            await context.Scheduler.StopAsync(CancellationToken.None);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(TaskScheduleKind.Interval)]
    [InlineData(TaskScheduleKind.Cron)]
    [InlineData(TaskScheduleKind.OneShot)]
    public async Task TickAsync_WaitsForOwnerEligibility_WithoutCreatingRunState(TaskScheduleKind kind)
    {
        var eligible = false;
        using var context = new SchedulerTestContext((task, _) => Task.FromResult(task.UserId == "user-2" || eligible));
        var blocked = SchedulerTestContext.CreateTask();
        blocked.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        blocked.Schedule.Kind = kind;
        blocked.Schedule.RunAt = blocked.CreatedAt;
        blocked.Schedule.CronExpression = "* * * * *";
        blocked.Schedule.TimeZoneId = "UTC";
        context.Repository.EnabledTasks.Add(blocked);
        var updates = new ConcurrentQueue<TaskStatusUpdate>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = context.TaskStatusNotifications.Subscribe(blocked.UserId, update =>
        {
            updates.Enqueue(update);
            if (update.Status == AgentTaskStatus.Completed)
            {
                completed.TrySetResult();
            }
            return Task.CompletedTask;
        });

        await context.Scheduler.TickAsync(CancellationToken.None);

        Assert.Equal(0, context.Executor.ExecutionCount);
        Assert.Empty(context.Repository.SavedRunStatuses);
        Assert.Empty(updates);
        Assert.False(context.Scheduler.TryGetLastRun(blocked.UserId, blocked.Id, out _));
        Assert.True(blocked.IsEnabled);
        Assert.DoesNotContain(context.LoggerProvider.Entries, entry => entry.Level >= LogLevel.Error);

        var control = SchedulerTestContext.CreateTask(userId: "user-2");
        context.Repository.EnabledTasks.Add(control);
        await context.Scheduler.TickAsync(CancellationToken.None);
        await WaitForExecutionCountAsync(context.Executor, control.Id, 1);
        Assert.Equal(0, context.Executor.GetExecutionCount(blocked.Id));
        Assert.Empty(updates);

        eligible = true;
        await context.Scheduler.TickAsync(CancellationToken.None);
        await WaitForExecutionCountAsync(context.Executor, blocked.Id, 1);
        Assert.True(context.Scheduler.TryGetLastRun(blocked.UserId, blocked.Id, out var run));
        Assert.Equal(TaskTriggerSource.Schedule, run!.TriggerSource);
        context.Executor.Release();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForSavedRunCountAsync(context.Repository, 2);
        await context.Scheduler.StopAsync(CancellationToken.None);
        Assert.Equal(AgentTaskStatus.Completed, run.Status);
    }

    [Fact]
    public async Task TickAsync_WhenEligibilityThrows_LogsFailureAndSchedulesOtherOwner()
    {
        using var context = new SchedulerTestContext((task, _) => task.UserId == "user-1"
            ? throw new IOException("Cannot read owner settings.")
            : Task.FromResult(true));
        var blocked = SchedulerTestContext.CreateTask();
        var control = SchedulerTestContext.CreateTask(userId: "user-2");
        context.Repository.EnabledTasks.AddRange([blocked, control]);

        await context.Scheduler.TickAsync(CancellationToken.None);

        Assert.False(context.Scheduler.TryGetLastRun(blocked.UserId, blocked.Id, out _));
        Assert.Equal(0, context.Executor.GetExecutionCount(blocked.Id));
        await WaitForExecutionCountAsync(context.Executor, control.Id, 1);
        var error = Assert.Single(context.LoggerProvider.Entries, entry => entry.Level == LogLevel.Error);
        Assert.IsType<IOException>(error.Exception);
        Assert.Contains(blocked.Id, error.Message, StringComparison.Ordinal);
        context.Executor.Release();
        await WaitForSavedRunCountAsync(context.Repository, 1);
        await context.Scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TriggerTaskAsync_StartsTaskImmediately_AndPublishesStartAndCompletionNotifications()
    {
        using var testContext = new SchedulerTestContext();
        var task = SchedulerTestContext.CreateTask();
        var startedNotificationReceived = new TaskCompletionSource<TaskStatusUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completedNotificationReceived = new TaskCompletionSource<TaskStatusUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = testContext.TaskStatusNotifications.Subscribe(task.UserId, update =>
        {
            if (update.TaskId != task.Id)
            {
                return Task.CompletedTask;
            }

            if (update.Status == AgentTaskStatus.Running)
            {
                startedNotificationReceived.TrySetResult(update);
            }

            if (update.Status == AgentTaskStatus.Completed)
            {
                completedNotificationReceived.TrySetResult(update);
            }

            return Task.CompletedTask;
        });

        var outcome = await testContext.Scheduler.TriggerTaskAsync(task);
        var startNotification = await startedNotificationReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(outcome.Started);
        Assert.False(string.IsNullOrWhiteSpace(outcome.RunId));
        Assert.True(testContext.Scheduler.IsTaskRunning(task.UserId, task.Id));
        Assert.True(testContext.Scheduler.TryGetLastRun(task.UserId, task.Id, out var run));
        Assert.NotNull(run);
        Assert.Equal(AgentTaskStatus.Running, run!.Status);
        Assert.Equal(task.Id, startNotification.TaskId);
        Assert.Equal(task.Name, startNotification.TaskName);
        Assert.Equal(AgentTaskStatus.Running, startNotification.Status);
        Assert.Equal("Task started manually.", startNotification.Message);

        testContext.Executor.Release();
        await testContext.Executor.ExecutionCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var completionNotification = await completedNotificationReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(testContext.Scheduler.IsTaskRunning(task.UserId, task.Id));
        Assert.Equal(task.Id, completionNotification.TaskId);
        Assert.Equal(task.Name, completionNotification.TaskName);
        Assert.Equal(AgentTaskStatus.Completed, completionNotification.Status);
        Assert.Null(completionNotification.Message);
    }

    [Fact]
    public async Task TriggerTaskAsync_WhenRunningSubscriberIsBlocked_DoesNotStartExecutionEarly()
    {
        using var testContext = new SchedulerTestContext();
        var task = SchedulerTestContext.CreateTask();
        var runningReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completedReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedStatuses = new ConcurrentQueue<AgentTaskStatus>();

        using var subscription = testContext.TaskStatusNotifications.Subscribe(task.UserId, async update =>
        {
            if (update.TaskId != task.Id)
            {
                return;
            }

            observedStatuses.Enqueue(update.Status);
            if (update.Status == AgentTaskStatus.Running)
            {
                runningReached.TrySetResult();
                await releaseRunning.Task;
            }
            else if (update.Status == AgentTaskStatus.Completed)
            {
                completedReached.TrySetResult();
            }
        });

        await testContext.Scheduler.TriggerTaskAsync(task);
        await runningReached.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(testContext.Executor.ExecutionStarted.Task.IsCompleted);

        releaseRunning.TrySetResult();
        await testContext.Executor.ExecutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        testContext.Executor.Release();
        await testContext.Executor.ExecutionCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await completedReached.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(
            [AgentTaskStatus.Running, AgentTaskStatus.Completed],
            observedStatuses.ToArray());
    }

    [Fact]
    public async Task StopAsync_WhenRunningSubscriberIsBlocked_DrainsWithoutStartingExecution()
    {
        using var testContext = new SchedulerTestContext();
        var task = SchedulerTestContext.CreateTask();
        var runningReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? stopTask = null;

        using var subscription = testContext.TaskStatusNotifications.Subscribe(task.UserId, async update =>
        {
            if (update.TaskId == task.Id && update.Status == AgentTaskStatus.Running)
            {
                runningReached.TrySetResult();
                await releaseRunning.Task;
            }
        });

        try
        {
            await testContext.Scheduler.TriggerTaskAsync(task);
            await runningReached.Task.WaitAsync(TimeSpan.FromSeconds(2));

            stopTask = testContext.Scheduler.StopAsync(CancellationToken.None);
            await stopTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(testContext.Executor.ExecutionStarted.Task.IsCompleted);
        }
        finally
        {
            releaseRunning.TrySetResult();
            if (stopTask is not null)
            {
                await stopTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
    }

    [Fact]
    public async Task TriggerTaskAsync_AllowsCaseVariantOwnerLookup()
    {
        using var testContext = new SchedulerTestContext();
        var task = SchedulerTestContext.CreateTask(userId: "user-1");

        await testContext.Scheduler.TriggerTaskAsync(task);
        await testContext.Executor.ExecutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(testContext.Scheduler.IsTaskRunning("USER-1", task.Id));
        Assert.True(testContext.Scheduler.TryGetLastRun("USER-1", task.Id, out var run));
        Assert.NotNull(run);

        testContext.Executor.Release();
        await testContext.Executor.ExecutionCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task TriggerTaskAsync_WhenTaskIsAlreadyRunning_ReturnsExistingRunId()
    {
        using var testContext = new SchedulerTestContext();
        var task = SchedulerTestContext.CreateTask();

        var firstStart = await testContext.Scheduler.TriggerTaskAsync(task);
        var secondStart = await testContext.Scheduler.TriggerTaskAsync(task);

        Assert.True(firstStart.Started);
        Assert.False(secondStart.Started);
        Assert.Equal(firstStart.RunId, secondStart.RunId);

        testContext.Executor.Release();
        await testContext.Executor.ExecutionCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task TriggerTaskAsync_WhenTaskIdIsReusedByAnotherUser_DoesNotCoalesceRuns()
    {
        using var testContext = new SchedulerTestContext();
        var firstOwnerTask = SchedulerTestContext.CreateTask(userId: "user-1");
        var firstStart = await testContext.Scheduler.TriggerTaskAsync(firstOwnerTask);
        await testContext.Executor.ExecutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var replacementOwnerTask = SchedulerTestContext.CreateTask(userId: "user-2");
        replacementOwnerTask.Id = firstOwnerTask.Id;
        var replacementStart = await testContext.Scheduler.TriggerTaskAsync(replacementOwnerTask);

        testContext.Executor.Release();
        await testContext.Executor.ExecutionCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(replacementStart.Started);
        Assert.NotEqual(firstStart.RunId, replacementStart.RunId);
    }

    [Theory]
    [InlineData(TaskTriggerSource.Manual)]
    [InlineData(TaskTriggerSource.ExternalWebhook)]
    public async Task TriggerTaskAsync_WhenTaskCompletes_QueuesSameOwnerAfterTaskDependents(TaskTriggerSource source)
    {
        using var testContext = new SchedulerTestContext((_, _) => throw new InvalidOperationException("Explicit and chained starts must bypass scheduled waiting."));
        var predecessor = SchedulerTestContext.CreateTask();
        var dependent = SchedulerTestContext.CreateTask("Dependent Task", predecessor.UserId);
        dependent.Schedule = new TaskSchedule
        {
            Kind = TaskScheduleKind.AfterTask,
            PredecessorTaskId = predecessor.Id
        };
        var otherUserDependent = SchedulerTestContext.CreateTask("Other User Dependent", "user-2");
        otherUserDependent.Schedule = new TaskSchedule
        {
            Kind = TaskScheduleKind.AfterTask,
            PredecessorTaskId = predecessor.Id
        };
        testContext.Repository.EnabledTasks.Add(dependent);
        testContext.Repository.EnabledTasks.Add(otherUserDependent);

        await testContext.Scheduler.TriggerTaskAsync(predecessor, source);
        testContext.Executor.Release();

        var dependentRun = await WaitForLastRunAsync(testContext.Scheduler, dependent.UserId, dependent.Id);
        await WaitForTaskToStopAsync(testContext.Scheduler, dependent);

        Assert.Equal(TaskTriggerSource.AfterTask, dependentRun.TriggerSource);
        Assert.Equal(AgentTaskStatus.Completed, dependentRun.Status);
        Assert.False(testContext.Scheduler.TryGetLastRun(otherUserDependent.UserId, otherUserDependent.Id, out _));
    }

    [Fact]
    public async Task TriggerTaskAsync_WhenDeleteAfterRunPredecessorCompletes_DeletesTaskAndRunsDependent()
    {
        using var testContext = new SchedulerTestContext();
        var predecessor = SchedulerTestContext.CreateTask();
        predecessor.Instruction = "Prepare the report.";
        predecessor.Schedule = new TaskSchedule
        {
            Kind = TaskScheduleKind.OneShot,
            RunAt = DateTimeOffset.UtcNow.AddMinutes(5),
            DeleteAfterRun = true
        };
        var dependent = SchedulerTestContext.CreateTask("Dependent Task", predecessor.UserId);
        dependent.Instruction = "Review the report.";
        dependent.Schedule = new TaskSchedule
        {
            Kind = TaskScheduleKind.AfterTask,
            PredecessorTaskId = predecessor.Id
        };
        await testContext.Repository.SaveTaskAsync(predecessor);
        testContext.Repository.EnabledTasks.Add(dependent);

        var outcome = await testContext.Scheduler.TriggerTaskAsync(predecessor);
        Assert.True(outcome.Started);
        testContext.Executor.Release();
        await testContext.Repository.DeleteCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(await testContext.Repository.GetTaskAsync(predecessor.UserId, predecessor.Id));
        await WaitForExecutionCountAsync(testContext.Executor, dependent.Id, 1);
        await WaitForTaskToStopAsync(testContext.Scheduler, dependent);
        var dependentRun = await WaitForLastRunAsync(testContext.Scheduler, dependent.UserId, dependent.Id);
        Assert.Equal(TaskTriggerSource.AfterTask, dependentRun.TriggerSource);
        Assert.Equal(AgentTaskStatus.Completed, dependentRun.Status);
        Assert.Equal(1, testContext.Executor.GetExecutionCount(dependent.Id));
    }

    [Fact]
    public async Task TriggerTaskAsync_WhenPredecessorCompletesDuringDependentRun_DropsOverlapWithoutPendingRerun()
    {
        using var testContext = new SchedulerTestContext();
        var predecessor = SchedulerTestContext.CreateTask();
        var dependent = SchedulerTestContext.CreateTask("Dependent Task", predecessor.UserId);
        dependent.Schedule = new TaskSchedule
        {
            Kind = TaskScheduleKind.AfterTask,
            PredecessorTaskId = predecessor.Id
        };
        testContext.Repository.EnabledTasks.Add(dependent);
        testContext.Executor.BlockTask(dependent.Id);

        await testContext.Scheduler.TriggerTaskAsync(dependent);
        await WaitForExecutionCountAsync(testContext.Executor, dependent.Id, 1);
        await testContext.Scheduler.TriggerTaskAsync(predecessor);
        testContext.Executor.Release();
        await WaitForTaskToStopAsync(testContext.Scheduler, predecessor);
        await Task.Delay(100);

        Assert.True(testContext.Scheduler.IsTaskRunning(dependent.UserId, dependent.Id));
        Assert.Equal(1, testContext.Executor.GetExecutionCount(dependent.Id));

        testContext.Executor.ReleaseTask(dependent.Id);
        await WaitForTaskToStopAsync(testContext.Scheduler, dependent);
        await Task.Delay(100);

        Assert.Equal(1, testContext.Executor.GetExecutionCount(dependent.Id));

        await testContext.Scheduler.TriggerTaskAsync(predecessor);
        await WaitForExecutionCountAsync(testContext.Executor, dependent.Id, 2);

        Assert.Equal(2, testContext.Executor.GetExecutionCount(dependent.Id));
    }

    [Fact]
    public async Task TriggerTaskAsync_WhenTaskFails_DoesNotQueueAfterTaskDependents()
    {
        using var testContext = new SchedulerTestContext();
        testContext.Executor.CompletionStatus = AgentTaskStatus.Failed;
        var predecessor = SchedulerTestContext.CreateTask();
        var dependent = SchedulerTestContext.CreateTask("Dependent Task", predecessor.UserId);
        dependent.Schedule = new TaskSchedule
        {
            Kind = TaskScheduleKind.AfterTask,
            PredecessorTaskId = predecessor.Id
        };
        testContext.Repository.EnabledTasks.Add(dependent);

        await testContext.Scheduler.TriggerTaskAsync(predecessor);
        testContext.Executor.Release();
        await testContext.Executor.ExecutionCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100);

        Assert.False(testContext.Scheduler.TryGetLastRun(dependent.UserId, dependent.Id, out _));
    }

    [Fact]
    public async Task TriggerTaskAsync_WhenDependentWouldCycle_DoesNotQueueAfterTaskDependent()
    {
        using var testContext = new SchedulerTestContext();
        var predecessor = SchedulerTestContext.CreateTask();
        var dependent = SchedulerTestContext.CreateTask("Dependent Task", predecessor.UserId);
        predecessor.Schedule = new TaskSchedule
        {
            Kind = TaskScheduleKind.AfterTask,
            PredecessorTaskId = dependent.Id
        };
        dependent.Schedule = new TaskSchedule
        {
            Kind = TaskScheduleKind.AfterTask,
            PredecessorTaskId = predecessor.Id
        };
        testContext.Repository.EnabledTasks.Add(dependent);

        await testContext.Scheduler.TriggerTaskAsync(predecessor);
        testContext.Executor.Release();
        await testContext.Executor.ExecutionCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100);

        Assert.False(testContext.Scheduler.TryGetLastRun(dependent.UserId, dependent.Id, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TriggerTaskAsync_WhenOneShotCompletes_PublishesFinalizedDefinition(bool deleteAfterRun)
    {
        using var context = new SchedulerTestContext();
        var task = SchedulerTestContext.CreateTask();
        task.Schedule = new TaskSchedule
        {
            Kind = TaskScheduleKind.OneShot,
            RunAt = DateTimeOffset.UtcNow.AddMinutes(5),
            DeleteAfterRun = deleteAfterRun
        };
        await context.Repository.SaveTaskAsync(task);
        var finalizedAtNotification = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = context.TaskStatusNotifications.Subscribe(task.UserId, async update =>
        {
            if (update.TaskId == task.Id && update.Status == AgentTaskStatus.Completed)
            {
                var saved = await context.Repository.GetTaskAsync(task.UserId, task.Id);
                finalizedAtNotification.TrySetResult(deleteAfterRun ? saved is null : saved?.IsEnabled == false);
            }
        });

        var outcome = await context.Scheduler.TriggerTaskAsync(task);
        Assert.True(outcome.Started);
        context.Executor.Release();

        Assert.True(await finalizedAtNotification.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task TriggerTaskAsync_WhenCompletedRunSaveFailsOnce_PreservesCompletionAndDoesNotRerunOneShot()
    {
        using var testContext = new SchedulerTestContext();
        var task = SchedulerTestContext.CreateTask();
        task.Schedule = new TaskSchedule
        {
            Kind = TaskScheduleKind.OneShot,
            RunAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            DeleteAfterRun = false
        };
        testContext.Repository.EnabledTasks.Add(task);
        testContext.Repository.SaveRunFailuresRemaining = 1;
        var observedStatuses = new ConcurrentQueue<AgentTaskStatus>();
        using var subscription = testContext.TaskStatusNotifications.Subscribe(task.UserId, update =>
        {
            if (update.TaskId == task.Id)
            {
                observedStatuses.Enqueue(update.Status);
            }

            return Task.CompletedTask;
        });

        await testContext.Scheduler.TriggerTaskAsync(task);
        testContext.Executor.Release();
        await WaitForSavedRunCountAsync(testContext.Repository, 2);

        Assert.All(testContext.Repository.SavedRunStatuses.ToArray(), status => Assert.Equal(AgentTaskStatus.Completed, status));
        Assert.DoesNotContain(AgentTaskStatus.Failed, observedStatuses);

        await testContext.Scheduler.TickAsync(CancellationToken.None);
        await Task.Delay(100);

        Assert.Equal(1, testContext.Executor.ExecutionCount);
    }

    [Fact]
    public async Task TriggerTaskAsync_WhenCompletedRunSaveFailsOnce_StillQueuesDependent()
    {
        using var testContext = new SchedulerTestContext();
        var predecessor = SchedulerTestContext.CreateTask();
        predecessor.Schedule = new TaskSchedule
        {
            Kind = TaskScheduleKind.OneShot,
            RunAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        await testContext.Repository.SaveTaskAsync(predecessor);
        var dependent = SchedulerTestContext.CreateTask("Dependent Task", predecessor.UserId);
        dependent.Schedule = new TaskSchedule
        {
            Kind = TaskScheduleKind.AfterTask,
            PredecessorTaskId = predecessor.Id
        };
        testContext.Repository.EnabledTasks.Add(dependent);
        testContext.Repository.SaveRunFailuresRemaining = 1;

        var outcome = await testContext.Scheduler.TriggerTaskAsync(predecessor);
        Assert.True(outcome.Started);
        testContext.Executor.Release();
        await WaitForSavedRunCountAsync(testContext.Repository, 2);

        var predecessorRun = await testContext.Repository.GetLatestRunAsync(predecessor.UserId, predecessor.Id);
        Assert.Equal(AgentTaskStatus.Completed, predecessorRun?.Status);
        var dependentRun = await WaitForLastRunAsync(testContext.Scheduler, dependent.UserId, dependent.Id);
        await WaitForTaskToStopAsync(testContext.Scheduler, dependent);
        Assert.Equal(TaskTriggerSource.AfterTask, dependentRun.TriggerSource);
        Assert.Equal(AgentTaskStatus.Completed, dependentRun.Status);
        Assert.Equal(1, testContext.Executor.GetExecutionCount(predecessor.Id));
        Assert.Equal(1, testContext.Executor.GetExecutionCount(dependent.Id));
    }

    [Fact]
    public async Task TriggerTaskAsync_WhenCompletedRunSaveFailsTwice_DoesNotQueueDependent()
    {
        using var testContext = new SchedulerTestContext();
        var predecessor = SchedulerTestContext.CreateTask();
        var dependent = SchedulerTestContext.CreateTask("Dependent Task", predecessor.UserId);
        dependent.Schedule = new TaskSchedule
        {
            Kind = TaskScheduleKind.AfterTask,
            PredecessorTaskId = predecessor.Id
        };
        testContext.Repository.EnabledTasks.Add(dependent);
        testContext.Repository.SaveRunFailuresRemaining = 2;
        var observedStatuses = new ConcurrentQueue<AgentTaskStatus>();
        using var subscription = testContext.TaskStatusNotifications.Subscribe(predecessor.UserId, update =>
        {
            observedStatuses.Enqueue(update.Status);
            return Task.CompletedTask;
        });

        testContext.Executor.Release();
        var outcome = await testContext.Scheduler.TriggerTaskAsync(predecessor);

        Assert.True(outcome.Started);
        Assert.Equal(2, testContext.Repository.SavedRunStatuses.Count);
        Assert.Null(await testContext.Repository.GetLatestRunAsync(predecessor.UserId, predecessor.Id));
        Assert.Equal([AgentTaskStatus.Running, AgentTaskStatus.Completed], observedStatuses.ToArray());
        Assert.False(testContext.Scheduler.TryGetLastRun(dependent.UserId, dependent.Id, out _));
        Assert.Equal(1, testContext.Executor.GetExecutionCount(predecessor.Id));
        Assert.Equal(0, testContext.Executor.GetExecutionCount(dependent.Id));
    }

    [Fact]
    public async Task TriggerTaskAsync_WhenOneShotDisableSaveFails_PreservesCompletedRun()
    {
        using var testContext = new SchedulerTestContext();
        var task = SchedulerTestContext.CreateTask();
        task.Schedule = new TaskSchedule
        {
            Kind = TaskScheduleKind.OneShot,
            RunAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            DeleteAfterRun = false
        };
        await testContext.Repository.SaveTaskAsync(task);
        testContext.Repository.FailNextSaveTask = true;
        var observedStatuses = new ConcurrentQueue<AgentTaskStatus>();
        using var subscription = testContext.TaskStatusNotifications.Subscribe(task.UserId, update =>
        {
            if (update.TaskId == task.Id)
            {
                observedStatuses.Enqueue(update.Status);
            }

            return Task.CompletedTask;
        });

        await testContext.Scheduler.TriggerTaskAsync(task);
        testContext.Executor.Release();
        await WaitForSavedRunCountAsync(testContext.Repository, 2);

        Assert.Equal(
            [AgentTaskStatus.Completed, AgentTaskStatus.Completed],
            testContext.Repository.SavedRunStatuses.ToArray());
        Assert.DoesNotContain(AgentTaskStatus.Failed, observedStatuses);
    }

    [Fact]
    public async Task TriggerTaskAsync_WhenIntervalRunFails_KeepsTaskEnabledWithoutSchedulerWarning()
    {
        using var testContext = new SchedulerTestContext();
        var task = SchedulerTestContext.CreateTask();
        await testContext.Repository.SaveTaskAsync(task);
        testContext.Executor.CompletionStatus = AgentTaskStatus.Failed;
        var observedStatuses = new ConcurrentQueue<AgentTaskStatus>();
        var failureReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = testContext.TaskStatusNotifications.Subscribe(task.UserId, update =>
        {
            if (update.TaskId == task.Id)
            {
                observedStatuses.Enqueue(update.Status);
                if (update.Status == AgentTaskStatus.Failed)
                {
                    failureReceived.TrySetResult();
                }
            }

            return Task.CompletedTask;
        });

        await testContext.Scheduler.TriggerTaskAsync(task);
        testContext.Executor.Release();
        await failureReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForTaskToStopAsync(testContext.Scheduler, task);

        var storedTask = await testContext.Repository.GetTaskAsync(task.UserId, task.Id);
        Assert.NotNull(storedTask);
        Assert.True(storedTask.IsEnabled);
        Assert.Equal([AgentTaskStatus.Running, AgentTaskStatus.Failed], observedStatuses.ToArray());
        Assert.DoesNotContain(
            testContext.LoggerProvider.Entries,
            entry => entry.Level >= LogLevel.Warning && entry.Message.Contains("finished with status", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TriggerTaskAsync_WhenDeleteAfterRunCompletes_RejectsStaleDefinition()
    {
        using var testContext = new SchedulerTestContext();
        var task = SchedulerTestContext.CreateTask();
        task.Schedule = new TaskSchedule
        {
            Kind = TaskScheduleKind.OneShot,
            RunAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            DeleteAfterRun = true
        };
        await testContext.Repository.SaveTaskAsync(task);

        var firstRun = await testContext.Scheduler.TriggerTaskAsync(task);
        Assert.True(firstRun.Started);
        testContext.Executor.Release();
        await WaitForTaskDeletionAsync(testContext.Repository);

        var staleReplacement = await testContext.Scheduler.TriggerTaskAsync(task);

        Assert.False(staleReplacement.Started);
        Assert.Null(testContext.Repository.StoredTask);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TriggerTaskAsync_WhenOneShotIsEditedDuringRun_PreservesEditedDefinition(bool deleteAfterRun)
    {
        using var testContext = new SchedulerTestContext();
        var task = SchedulerTestContext.CreateTask();
        task.Instruction = "Original instruction";
        task.Schedule = new TaskSchedule
        {
            Kind = TaskScheduleKind.OneShot,
            RunAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            DeleteAfterRun = deleteAfterRun
        };
        await testContext.Repository.SaveTaskAsync(task);

        await testContext.Scheduler.TriggerTaskAsync(task);
        await testContext.Executor.ExecutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var editedTask = SchedulerTestContext.CreateTask("Edited Task", task.UserId);
        editedTask.Id = task.Id;
        editedTask.Instruction = "Edited instruction";
        editedTask.Schedule = new TaskSchedule
        {
            Kind = TaskScheduleKind.Interval,
            IntervalMinutes = 15
        };
        await testContext.Repository.SaveTaskAsync(editedTask);
        testContext.Repository.ArmPostRunBookkeeping();

        testContext.Executor.Release();
        await testContext.Repository.PostRunBookkeepingReached.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var storedTask = await testContext.Repository.GetTaskAsync(task.UserId, task.Id);
        Assert.NotNull(storedTask);
        Assert.Equal(editedTask.Name, storedTask.Name);
        Assert.Equal(editedTask.Instruction, storedTask.Instruction);
        Assert.Equal(TaskScheduleKind.Interval, storedTask.Schedule.Kind);
        Assert.Equal(15, storedTask.Schedule.IntervalMinutes);
        Assert.True(storedTask.IsEnabled);
    }

    [Fact]
    public async Task TriggerTaskAsync_WhenOneShotIsEditedDuringFinalization_PreservesEditedDefinition()
    {
        using var testContext = new SchedulerTestContext();
        var task = SchedulerTestContext.CreateTask();
        task.Schedule = new TaskSchedule
        {
            Kind = TaskScheduleKind.OneShot,
            RunAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            DeleteAfterRun = true
        };
        await testContext.Repository.SaveTaskAsync(task);

        var deleteReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        testContext.Repository.BeforeDeleteAsync = async ct =>
        {
            deleteReached.TrySetResult();
            await releaseDelete.Task.WaitAsync(ct);
        };

        await testContext.Scheduler.TriggerTaskAsync(task);
        testContext.Executor.Release();
        await deleteReached.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var editedTask = SchedulerTestContext.CreateTask("Edited Task", task.UserId);
        editedTask.Id = task.Id;
        editedTask.Instruction = "Edited instruction";
        var saveTask = testContext.Scheduler.RunTaskSaveAsync(
            editedTask.UserId,
            editedTask.Id,
            async ct =>
            {
                await testContext.Repository.SaveTaskAsync(editedTask, ct);
                return editedTask;
            });
        releaseDelete.TrySetResult();
        await Task.WhenAll(saveTask, testContext.Repository.DeleteCompleted.Task)
            .WaitAsync(TimeSpan.FromSeconds(2));

        var storedTask = await testContext.Repository.GetTaskAsync(task.UserId, task.Id);
        Assert.NotNull(storedTask);
        Assert.Equal(editedTask.Name, storedTask.Name);
        Assert.Equal(TaskScheduleKind.Interval, storedTask.Schedule.Kind);
    }

    private static async Task<AgentTaskRun> WaitForLastRunAsync(AgentTaskSchedulerService scheduler, string userId, string taskId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (scheduler.TryGetLastRun(userId, taskId, out var run) && run is not null)
            {
                return run;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }

        throw new TimeoutException($"Task '{taskId}' was not queued.");
    }

    private static async Task WaitForSavedRunCountAsync(TestAgentTaskRepository repository, int count)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (repository.SavedRunStatuses.Count >= count)
            {
                return;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }

        throw new TimeoutException($"Expected {count} saved run attempts.");
    }

    private static async Task WaitForTaskDeletionAsync(TestAgentTaskRepository repository)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (repository.StoredTask is null)
            {
                return;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }

        throw new TimeoutException("The one-shot task definition was not deleted.");
    }

    private static async Task WaitForExecutionCountAsync(TestAgentTaskExecutor executor, string taskId, int count)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (executor.GetExecutionCount(taskId) >= count)
            {
                return;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }

        throw new TimeoutException($"Task '{taskId}' did not reach {count} executions.");
    }

    private static async Task WaitForTaskToStopAsync(
        AgentTaskSchedulerService scheduler,
        AgentTaskDefinition task)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!scheduler.IsTaskRunning(task.UserId, task.Id))
            {
                return;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }

        throw new TimeoutException($"Task '{task.Id}' did not stop.");
    }

    private sealed class SchedulerTestContext : IDisposable
    {
        public TestLoggerProvider LoggerProvider { get; } = new();

        public SchedulerTestContext(Func<AgentTaskDefinition, CancellationToken, Task<bool>>? canScheduleTaskAsync = null)
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.AddProvider(LoggerProvider));
            services.AddScoped<IAgentTaskRepository>(_ => Repository);
            services.AddScoped<IAgentTaskExecutor>(_ => Executor);

            services.AddSingleton<NotificationHub<TaskStatusUpdate>>();

            var provider = services.BuildServiceProvider();
            ServiceProvider = provider;
            TaskStatusNotifications = provider.GetRequiredService<NotificationHub<TaskStatusUpdate>>();
            Scheduler = new AgentTaskSchedulerService(
                provider,
                TaskStatusNotifications,
                provider.GetRequiredService<ILogger<AgentTaskSchedulerService>>(),
                TimeProvider.System,
                canScheduleTaskAsync);
        }

        public ServiceProvider ServiceProvider { get; }
        public TestAgentTaskRepository Repository { get; } = new();
        public TestAgentTaskExecutor Executor { get; } = new();
        public NotificationHub<TaskStatusUpdate> TaskStatusNotifications { get; }
        public AgentTaskSchedulerService Scheduler { get; }

        public static AgentTaskDefinition CreateTask(string name = "Debug Task", string userId = "user-1")
        {
            return new AgentTaskDefinition
            {
                Id = Guid.NewGuid().ToString(),
                Name = name,
                UserId = userId,
                Schedule = new TaskSchedule
                {
                    Kind = TaskScheduleKind.Interval,
                    IntervalMinutes = 60
                }
            };
        }

        public void Dispose()
        {
            ServiceProvider.Dispose();
            LoggerProvider.Dispose();
        }
    }

    private sealed class TestAgentTaskRepository : IAgentTaskRepository
    {
        public List<AgentTaskDefinition> EnabledTasks { get; } = [];
        public ConcurrentQueue<AgentTaskStatus> SavedRunStatuses { get; } = [];
        public int SaveRunFailuresRemaining { get; set; }
        public bool FailNextSaveTask { get; set; }
        public Func<CancellationToken, Task>? BeforeDeleteAsync { get; set; }
        public TaskCompletionSource DeleteCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AgentTaskDefinition? StoredTask { get; private set; }
        public TaskCompletionSource PostRunBookkeepingReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private volatile bool _postRunBookkeepingArmed;

        private AgentTaskStatus? _latestRunStatus;

        public async Task DeleteTaskAsync(string userId, string taskId, CancellationToken ct = default)
        {
            if (BeforeDeleteAsync is not null)
            {
                await BeforeDeleteAsync(ct);
            }

            StoredTask = null;
            DeleteCompleted.TrySetResult();
        }
        public Task<IReadOnlyList<AgentTaskDefinition>> GetAllEnabledTasksAsync(CancellationToken ct = default)
        {
            if (_postRunBookkeepingArmed)
            {
                PostRunBookkeepingReached.TrySetResult();
            }

            return Task.FromResult<IReadOnlyList<AgentTaskDefinition>>(EnabledTasks);
        }
        public Task<JsonElement?> GetLatestSessionAsync(string userId, string taskId, CancellationToken ct = default) =>
            Task.FromResult<JsonElement?>(null);

        public Task<AgentTaskRun?> GetLatestRunAsync(string userId, string taskId, CancellationToken ct = default) =>
            Task.FromResult(_latestRunStatus is null
                ? null
                : new AgentTaskRun { UserId = userId, TaskId = taskId, Status = _latestRunStatus.Value });
        public Task<IReadOnlyDictionary<string, AgentTaskRun>> GetLatestRunsAsync(IEnumerable<string> taskIds, CancellationToken ct = default) => Task.FromResult<IReadOnlyDictionary<string, AgentTaskRun>>(new Dictionary<string, AgentTaskRun>());
        public Task<IReadOnlyList<AgentTaskRun>> GetRunsAsync(string userId, string taskId, int limit = 20, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AgentTaskRun>>([]);
        public Task<AgentTaskDefinition?> GetTaskAsync(string userId, string taskId, CancellationToken ct = default) =>
            Task.FromResult(StoredTask is not null &&
                            string.Equals(StoredTask.UserId, userId, StringComparison.Ordinal) &&
                            string.Equals(StoredTask.Id, taskId, StringComparison.Ordinal)
                ? StoredTask
                : null);
        public Task<AgentTaskDefinition?> GetTaskByExternalTriggerSlugAsync(string slug, CancellationToken ct = default) => Task.FromResult<AgentTaskDefinition?>(null);
        public Task<IReadOnlyList<AgentTaskDefinition>> GetTasksByUserAsync(string userId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AgentTaskDefinition>>([]);
        public Task<AgentTaskRun?> GetRunAsync(string userId, string taskId, string runId, CancellationToken ct = default) => Task.FromResult<AgentTaskRun?>(null);
        public Task SaveRunAsync(AgentTaskRun run, CancellationToken ct = default)
        {
            SavedRunStatuses.Enqueue(run.Status);
            if (SaveRunFailuresRemaining > 0)
            {
                SaveRunFailuresRemaining--;
                throw new IOException("Transient run persistence failure.");
            }

            _latestRunStatus = run.Status;
            return Task.CompletedTask;
        }
        public Task SaveTaskAsync(AgentTaskDefinition task, CancellationToken ct = default, string? creationDirectoryName = null)
        {
            if (FailNextSaveTask)
            {
                FailNextSaveTask = false;
                throw new IOException("Transient task persistence failure.");
            }

            StoredTask = task;
            return Task.CompletedTask;
        }

        public void ArmPostRunBookkeeping()
        {
            _postRunBookkeepingArmed = true;
        }
        public AgentTaskDefinition ParseDefinition(string ownerUserIdFromCatalog, string fileText) => throw new NotSupportedException();
    }

    private sealed class TestAgentTaskExecutor : IAgentTaskExecutor
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _taskGates = [];
        private readonly ConcurrentDictionary<string, int> _executionCounts = [];
        public TaskCompletionSource ExecutionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ExecutionCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AgentTaskStatus CompletionStatus { get; set; } = AgentTaskStatus.Completed;
        private int _executionCount;

        public int ExecutionCount => Volatile.Read(ref _executionCount);

        public async Task<AgentTaskRun> ExecuteAsync(AgentTaskDefinition task, AgentTaskRun? run = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _executionCount);
            _executionCounts.AddOrUpdate(task.Id, 1, static (_, count) => count + 1);
            var currentRun = run ?? new AgentTaskRun
            {
                TaskId = task.Id,
                UserId = task.UserId,
                Status = AgentTaskStatus.Running,
                StartedAt = DateTimeOffset.UtcNow,
                LiveMessages = [],
            };

            ExecutionStarted.TrySetResult();
            var gate = _taskGates.GetValueOrDefault(task.Id) ?? _gate;
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);

            currentRun.Status = CompletionStatus;
            currentRun.CompletedAt = DateTimeOffset.UtcNow;
            ExecutionCompleted.TrySetResult();
            return currentRun;
        }

        public void Release()
        {
            _gate.TrySetResult();
        }

        public void BlockTask(string taskId) =>
            _taskGates[taskId] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseTask(string taskId)
        {
            if (_taskGates.TryGetValue(taskId, out var gate))
            {
                gate.TrySetResult();
            }
        }

        public int GetExecutionCount(string taskId) => _executionCounts.GetValueOrDefault(taskId);
    }
}
