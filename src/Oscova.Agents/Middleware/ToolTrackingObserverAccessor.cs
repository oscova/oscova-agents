namespace Oscova.Agents.Middleware;

/// <summary>
/// Holds a mutable reference to the current <see cref="IToolTrackingObserver"/>.
/// Set before each agent run and cleared after, allowing a cached agent's
/// middleware to observe the per-request parser instance.
/// </summary>
public class ToolTrackingObserverAccessor
{
    /// <summary>
    /// Gets or sets the observer for the current tool-tracking run.
    /// </summary>
    public IToolTrackingObserver? Observer { get; set; }
}
