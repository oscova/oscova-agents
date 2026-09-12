namespace Oscova.Agents.Hooks;

/// <summary>
/// Requests dispatch of one hook event through the configured external handlers.
/// </summary>
public sealed class HookDispatchRequest
{
    /// <summary>
    /// Envelope whose lifecycle event is matched and sent to external handlers.
    /// </summary>
    public required HookEnvelope Envelope { get; init; }

    /// <summary>
    /// True when a deny decision should stop the caller's lifecycle event.
    /// </summary>
    public bool IsBlocking { get; init; }
}
