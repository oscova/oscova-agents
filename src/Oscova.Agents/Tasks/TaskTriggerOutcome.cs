namespace Oscova.Agents.Tasks
{
    /// <summary>
    /// Result returned when a task run is queued or rejected before execution starts.
    /// </summary>
    public sealed class TaskTriggerOutcome
    {
        /// <summary>
        /// Gets whether a new run was started.
        /// </summary>
        public bool Started { get; init; }

        /// <summary>
        /// Gets the queued or already-running task run identifier.
        /// </summary>
        public string? RunId { get; init; }

        /// <summary>
        /// Gets the current status for the queued or already-running run.
        /// </summary>
        public AgentTaskStatus Status { get; init; } = AgentTaskStatus.Pending;

        /// <summary>
        /// Gets a human-readable trigger result message.
        /// </summary>
        public string Message { get; init; } = string.Empty;
    }
}
