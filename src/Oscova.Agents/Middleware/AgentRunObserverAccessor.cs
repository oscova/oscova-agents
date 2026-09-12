namespace Oscova.Agents.Middleware;

/// <summary>
/// Holds a mutable reference to the current <see cref="IAgentRunObserver"/>.
/// Set before each agent run and cleared after, allowing a cached agent's
/// middleware to target the per-run observer without rebuilding the pipeline.
/// </summary>
public class AgentRunObserverAccessor
{
    /// <summary>
    /// Gets or sets the observer for the current agent run.
    /// </summary>
    public IAgentRunObserver? Observer { get; set; }
}
