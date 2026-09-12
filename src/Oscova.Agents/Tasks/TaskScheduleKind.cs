namespace Oscova.Agents.Tasks
{
    /// <summary>
    /// Describes the scheduling strategy for an agent task.
    /// </summary>
    public enum TaskScheduleKind
    {
        /// <summary>
        /// Run repeatedly after a fixed number of minutes has elapsed.
        /// </summary>
        Interval,

        /// <summary>
        /// Run once at a specific time and then disable or delete the task.
        /// </summary>
        OneShot,

        /// <summary>
        /// Run according to a cron expression in the configured time zone.
        /// </summary>
        Cron,

        /// <summary>
        /// Run automatically after another task completes successfully.
        /// </summary>
        AfterTask
    }
}
