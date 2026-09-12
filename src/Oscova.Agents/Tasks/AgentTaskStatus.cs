namespace Oscova.Agents.Tasks
{
    /// <summary>
    /// Describes the lifecycle status of an agent task run.
    /// </summary>
    public enum AgentTaskStatus
    {
        /// <summary>
        /// The run has been created but has not started execution.
        /// </summary>
        Pending,

        /// <summary>
        /// The run is currently executing.
        /// </summary>
        Running,

        /// <summary>
        /// The run finished successfully.
        /// </summary>
        Completed,

        /// <summary>
        /// The run ended with an error or cancellation.
        /// </summary>
        Failed,

        /// <summary>
        /// The run was deliberately not executed because a required precondition was not met.
        /// </summary>
        Skipped
    }
}
