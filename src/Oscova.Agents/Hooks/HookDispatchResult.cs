namespace Oscova.Agents.Hooks;

/// <summary>
/// Normalized hook result returned to the caller that requested dispatch.
/// </summary>
public sealed class HookDispatchResult
{
    /// <summary>
    /// Normalized allow or deny decision.
    /// </summary>
    public HookDecision Decision { get; init; } = HookDecision.Allow;

    /// <summary>
    /// Optional machine-readable reason for the decision.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Optional human-readable message from the handler or transport layer.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// True when the result came from transport or handler execution failure.
    /// </summary>
    public bool IsTransportFailure { get; init; }

    /// <summary>
    /// True when <see cref="Decision" /> allows execution to continue.
    /// </summary>
    public bool IsAllowed => Decision == HookDecision.Allow;

    /// <summary>
    /// Creates an allow result.
    /// </summary>
    public static HookDispatchResult Allow() => new();

    /// <summary>
    /// Creates a deny result with optional reason metadata.
    /// </summary>
    public static HookDispatchResult Deny(string message, string? reason = null) => new()
    {
        Decision = HookDecision.Deny,
        Reason = reason,
        Message = message
    };

    /// <summary>
    /// Creates a transport-failure result before failure-mode mapping is applied.
    /// </summary>
    public static HookDispatchResult TransportFailure(string message, string? reason = null) => new()
    {
        Decision = HookDecision.Deny,
        IsTransportFailure = true,
        Reason = reason,
        Message = message
    };
}