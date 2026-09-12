namespace Oscova.Agents.Tasks
{
    /// <summary>
    /// Describes whether scheduled task runs reuse conversation session state.
    /// </summary>
    public enum TaskSessionMode
    {
        /// <summary>
        /// Each task run starts with an isolated session.
        /// </summary>
        Isolated,

        /// <summary>
        /// Task runs can persist and reuse session state across executions.
        /// </summary>
        Persistent
    }
}
