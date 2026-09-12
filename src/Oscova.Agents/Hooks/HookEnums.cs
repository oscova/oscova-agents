namespace Oscova.Agents.Hooks;

/// <summary>
/// Selects how a hook definition is executed.
/// </summary>
public enum HookHandlerType
{
    /// <summary>
    /// Runs a local process and sends the hook envelope through standard input.
    /// </summary>
    Command = 0,

    /// <summary>
    /// Sends the hook envelope to an outbound HTTP endpoint.
    /// </summary>
    Http = 1
}

/// <summary>
/// Normalized decision returned by an external hook.
/// </summary>
public enum HookDecision
{
    /// <summary>
    /// Allows the lifecycle event to continue.
    /// </summary>
    Allow = 0,

    /// <summary>
    /// Blocks the lifecycle event when dispatch is blocking.
    /// </summary>
    Deny = 1
}

/// <summary>
/// Determines how transport or handler failures affect blocking hooks.
/// </summary>
public enum HookFailureMode
{
    /// <summary>
    /// Treats hook transport failures as allowed.
    /// </summary>
    FailOpen = 0,

    /// <summary>
    /// Treats hook transport failures as denied.
    /// </summary>
    FailClosed = 1
}