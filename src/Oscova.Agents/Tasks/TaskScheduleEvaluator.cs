using System;
using Cronos;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Tasks
{
    /// <summary>
    /// Evaluates task schedules against the latest run state and current time.
    /// </summary>
    public static class TaskScheduleEvaluator
    {
        /// <summary>
        /// Determines whether a task is due to run now.
        /// </summary>
        /// <param name="task">The task definition and schedule to evaluate.</param>
        /// <param name="latestRun">The latest known run for the task, if any.</param>
        /// <param name="now">The current UTC timestamp used for evaluation.</param>
        /// <returns><see langword="true"/> when the task should start a new run.</returns>
        public static bool IsDue(AgentTaskDefinition task, AgentTaskRun? latestRun, DateTimeOffset now)
        {
            var schedule = task.Schedule;

            switch (schedule.Kind)
            {
                case TaskScheduleKind.Interval when schedule.IntervalMinutes.HasValue:
                    if (schedule.IntervalMinutes.Value <= 0)
                    {
                        return false;
                    }

                    // Never ran → due immediately
                    if (latestRun is null)
                    {
                        return true;
                    }

                    // Still running → not due
                    if (latestRun.Status == AgentTaskStatus.Running)
                    {
                        return false;
                    }

                    // Check if enough time has elapsed since the last completed/failed run
                    var reference = latestRun.CompletedAt ?? latestRun.StartedAt;
                    if (reference is null)
                    {
                        return true;
                    }

                    return reference.Value.AddMinutes(schedule.IntervalMinutes.Value) <= now;


                case TaskScheduleKind.OneShot when schedule.RunAt.HasValue:
                    if (schedule.RunAt.Value > now)
                    {
                        return false;
                    }

                    return latestRun is null;
                case TaskScheduleKind.Cron when !string.IsNullOrWhiteSpace(schedule.CronExpression):
                    if (latestRun?.Status == AgentTaskStatus.Running)
                    {
                        return false;
                    }

                    CronExpression expression;
                    try
                    {
                        expression = CronExpression.Parse(schedule.CronExpression);
                    }
                    catch (CronFormatException)
                    {
                        return false;
                    }

                    var timeZone = TimeZoneResolver.ResolveOrLocal(schedule.TimeZoneId);
                    var anchor = latestRun?.CompletedAt
                                 ?? latestRun?.StartedAt
                                 // Use just before creation so GetNextOccurrence finds the first cron slot
                                 // that should have applied to a never-run task.
                                 ?? task.CreatedAt.AddSeconds(-1);
                    var nextOccurrence = expression.GetNextOccurrence(anchor, timeZone);
                    return nextOccurrence <= now;


                case TaskScheduleKind.AfterTask:
                    // Chained tasks are not time-driven; the scheduler queues them when the predecessor completes.
                    return false;

                default:
                    return false;
            }
        }
    }
}
