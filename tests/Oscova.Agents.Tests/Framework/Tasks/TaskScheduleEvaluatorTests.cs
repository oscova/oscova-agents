using Oscova.Agents.Tasks;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Tests.Framework.Tasks
{
    public class TaskScheduleEvaluatorTests
    {
        private static readonly DateTimeOffset Now = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

        private static AgentTaskDefinition IntervalTask(int intervalMinutes) => new()
        {
            Schedule = new TaskSchedule
            {
                Kind = TaskScheduleKind.Interval,
                IntervalMinutes = intervalMinutes,
            },
        };

        private static AgentTaskDefinition OneShotTask(DateTimeOffset runAt) => new()
        {
            Schedule = new TaskSchedule
            {
                Kind = TaskScheduleKind.OneShot,
                RunAt = runAt,
            },
        };

        private static AgentTaskDefinition CronTask(string expression, string timeZoneId, DateTimeOffset? createdAt = null) => new()
        {
            CreatedAt = createdAt ?? Now.AddHours(-2),
            Schedule = new TaskSchedule
            {
                Kind = TaskScheduleKind.Cron,
                CronExpression = expression,
                TimeZoneId = timeZoneId,
            },
        };

        private static AgentTaskRun RunWithStatus(AgentTaskStatus status, DateTimeOffset? completedAt = null) => new()
        {
            Status = status,
            StartedAt = Now.AddMinutes(-10),
            CompletedAt = completedAt,
        };

        // ── Interval ──────────────────────────────────────────────────────────

        [Fact]
        public void Interval_NeverRan_IsDue()
        {
            var task = IntervalTask(60);
            Assert.True(TaskScheduleEvaluator.IsDue(task, null, Now));
        }

        [Fact]
        public void Interval_StillRunning_IsNotDue()
        {
            var task = IntervalTask(60);
            var run = RunWithStatus(AgentTaskStatus.Running);
            Assert.False(TaskScheduleEvaluator.IsDue(task, run, Now));
        }

        [Fact]
        public void Interval_CompletedRecently_IsNotDue()
        {
            var task = IntervalTask(60);
            var run = RunWithStatus(AgentTaskStatus.Completed, completedAt: Now.AddMinutes(-30));
            Assert.False(TaskScheduleEvaluator.IsDue(task, run, Now));
        }

        [Fact]
        public void Interval_CompletedExactlyAtInterval_IsDue()
        {
            var task = IntervalTask(60);
            var run = RunWithStatus(AgentTaskStatus.Completed, completedAt: Now.AddMinutes(-60));
            Assert.True(TaskScheduleEvaluator.IsDue(task, run, Now));
        }

        [Fact]
        public void Interval_CompletedPastInterval_IsDue()
        {
            var task = IntervalTask(60);
            var run = RunWithStatus(AgentTaskStatus.Completed, completedAt: Now.AddMinutes(-90));
            Assert.True(TaskScheduleEvaluator.IsDue(task, run, Now));
        }

        [Fact]
        public void Interval_FailedPastInterval_IsDue()
        {
            var task = IntervalTask(60);
            var run = RunWithStatus(AgentTaskStatus.Failed, completedAt: Now.AddMinutes(-90));
            Assert.True(TaskScheduleEvaluator.IsDue(task, run, Now));
        }

        [Fact]
        public void Interval_ZeroInterval_IsNotDue()
        {
            var task = IntervalTask(0);
            Assert.False(TaskScheduleEvaluator.IsDue(task, null, Now));
        }

        [Fact]
        public void Interval_NegativeInterval_IsNotDue()
        {
            var task = IntervalTask(-5);
            Assert.False(TaskScheduleEvaluator.IsDue(task, null, Now));
        }

        [Fact]
        public void Interval_NoIntervalMinutes_IsNotDue()
        {
            var task = new AgentTaskDefinition
            {
                Schedule = new TaskSchedule { Kind = TaskScheduleKind.Interval, IntervalMinutes = null },
            };
            Assert.False(TaskScheduleEvaluator.IsDue(task, null, Now));
        }

        // ── OneShot ───────────────────────────────────────────────────────────

        [Fact]
        public void OneShot_RunAtInFuture_IsNotDue()
        {
            var task = OneShotTask(Now.AddMinutes(1));
            Assert.False(TaskScheduleEvaluator.IsDue(task, null, Now));
        }

        [Fact]
        public void OneShot_RunAtNow_NeverRan_IsDue()
        {
            var task = OneShotTask(Now);
            Assert.True(TaskScheduleEvaluator.IsDue(task, null, Now));
        }

        [Fact]
        public void OneShot_RunAtInPast_NeverRan_IsDue()
        {
            var task = OneShotTask(Now.AddMinutes(-5));
            Assert.True(TaskScheduleEvaluator.IsDue(task, null, Now));
        }

        [Fact]
        public void OneShot_AlreadyCompleted_IsNotDue()
        {
            var task = OneShotTask(Now.AddMinutes(-5));
            var run = RunWithStatus(AgentTaskStatus.Completed);
            Assert.False(TaskScheduleEvaluator.IsDue(task, run, Now));
        }

        [Fact]
        public void OneShot_PreviousRunFailed_IsNotDue()
        {
            var task = OneShotTask(Now.AddMinutes(-5));
            var run = RunWithStatus(AgentTaskStatus.Failed);
            Assert.False(TaskScheduleEvaluator.IsDue(task, run, Now));
        }

        [Fact]
        public void OneShot_StillRunning_IsNotDue()
        {
            var task = OneShotTask(Now.AddMinutes(-5));
            var run = RunWithStatus(AgentTaskStatus.Running);
            Assert.False(TaskScheduleEvaluator.IsDue(task, run, Now));
        }

        [Fact]
        public void OneShot_NoRunAt_IsNotDue()
        {
            var task = new AgentTaskDefinition
            {
                Schedule = new TaskSchedule { Kind = TaskScheduleKind.OneShot, RunAt = null },
            };
            Assert.False(TaskScheduleEvaluator.IsDue(task, null, Now));
        }

        [Fact]
        public void Cron_InvalidExpression_IsNotDue()
        {
            var task = CronTask("not-a-cron", "UTC");
            Assert.False(TaskScheduleEvaluator.IsDue(task, null, Now));
        }

        [Fact]
        public void Cron_NeverRan_WhenOccurrenceHasPassed_IsDue()
        {
            var task = CronTask("0 * * * *", "UTC", Now.AddHours(-3));
            Assert.True(TaskScheduleEvaluator.IsDue(task, null, Now));
        }

        [Fact]
        public void Cron_BeforeNextOccurrence_IsNotDue()
        {
            var task = CronTask("30 23 * * *", "UTC", Now.AddHours(-2));
            Assert.False(TaskScheduleEvaluator.IsDue(task, null, Now));
        }

        [Fact]
        public void Cron_StillRunning_IsNotDue()
        {
            var task = CronTask("* * * * *", "UTC");
            var run = RunWithStatus(AgentTaskStatus.Running);
            Assert.False(TaskScheduleEvaluator.IsDue(task, run, Now));
        }

        [Fact]
        public void Cron_ResolvesIanaAndWindowsIds()
        {
            var ianaTokyo = TimeZoneResolver.ResolveOrLocal("Asia/Tokyo");
            var windowsTokyo = TimeZoneResolver.ResolveOrLocal("Tokyo Standard Time");

            Assert.NotNull(ianaTokyo);
            Assert.NotNull(windowsTokyo);
            Assert.Equal(ianaTokyo.BaseUtcOffset, windowsTokyo.BaseUtcOffset);

            var tokyoTask = CronTask("0 9 * * *", "Asia/Tokyo");
            _ = TaskScheduleEvaluator.IsDue(tokyoTask, null, Now);
        }

        [Fact]
        public void AfterTask_IsNeverDueFromScheduleEvaluation()
        {
            var task = new AgentTaskDefinition
            {
                Schedule = new TaskSchedule
                {
                    Kind = TaskScheduleKind.AfterTask,
                    PredecessorTaskId = "task-1",
                },
            };

            Assert.False(TaskScheduleEvaluator.IsDue(task, null, Now));
            Assert.False(TaskScheduleEvaluator.IsDue(task, RunWithStatus(AgentTaskStatus.Completed, Now.AddMinutes(-1)), Now));
        }
    }
}
