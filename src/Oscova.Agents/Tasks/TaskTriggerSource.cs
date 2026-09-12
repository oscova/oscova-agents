namespace Oscova.Agents.Tasks
{
    /// <summary>
    /// Identifies how a task run was started.
    /// </summary>
    public enum TaskTriggerSource
    {
        /// <summary>
        /// The scheduler started the task because its schedule became due.
        /// </summary>
        Schedule = 0,

        /// <summary>
        /// An authenticated user manually started the task.
        /// </summary>
        Manual = 1,

        /// <summary>
        /// An inbound hook API caller started the task.
        /// </summary>
        ExternalWebhook = 2,

        /// <summary>
        /// A predecessor task completed and chained into this task.
        /// </summary>
        AfterTask = 3
    }
}
