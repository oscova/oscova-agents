namespace Oscova.Agents.Tasks
{
    /// <summary>
    /// Controls how the user is notified after a task run completes.
    /// </summary>
    public enum TaskNotificationMode
    {
        /// <summary>
        /// Do not send automatic notifications for task outcomes.
        /// </summary>
        Never,

        /// <summary>
        /// Notify after every completed, skipped, or failed task run.
        /// </summary>
        Always,

        /// <summary>
        /// Notify only when the task fails or needs attention.
        /// </summary>
        OnlyOnFailure
    }
}
