using System;

namespace Oscova.Agents.Tasks
{
    /// <summary>
    /// Defines when and how an agent task should run.
    /// </summary>
    public class TaskSchedule
    {
        /// <summary>
        /// Gets or sets the scheduling strategy.
        /// </summary>
        public TaskScheduleKind Kind { get; set; } = TaskScheduleKind.Interval;

        /// <summary>
        /// Gets or sets the interval in minutes for interval schedules.
        /// </summary>
        public int? IntervalMinutes { get; set; } = 60;

        /// <summary>
        /// Gets or sets the due time for one-shot schedules.
        /// </summary>
        public DateTimeOffset? RunAt { get; set; }

        /// <summary>
        /// Gets or sets the cron expression for cron schedules.
        /// </summary>
        public string? CronExpression { get; set; }

        /// <summary>
        /// Gets or sets the time-zone identifier used to evaluate cron schedules.
        /// </summary>
        public string? TimeZoneId { get; set; }

        /// <summary>
        /// Gets or sets whether a successful one-shot task should be deleted instead of disabled.
        /// </summary>
        public bool DeleteAfterRun { get; set; }

        /// <summary>
        /// Gets or sets the identifier of the predecessor task whose successful completion
        /// triggers this task. Only used when <see cref="Kind"/> is <see cref="TaskScheduleKind.AfterTask"/>.
        /// </summary>
        public string? PredecessorTaskId { get; set; }
    }
}
