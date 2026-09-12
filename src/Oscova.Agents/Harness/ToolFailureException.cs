using System;

namespace Oscova.Agents.Harness;

/// <summary>Signals a tool failure whose text is written for the agent to read.</summary>
/// <remarks>Reason and fix text must not contain paths, host names, connection strings, credentials, or raw exception text.</remarks>
public sealed class ToolFailureException : Exception
{
    /// <summary>Creates a failure with an agent-readable reason and optional next step.</summary>
    /// <param name="reason">The safe reason for the failure.</param>
    /// <param name="fix">The safe next step, when one is available.</param>
    public ToolFailureException(string reason, string? fix = null)
        : base(reason)
    {
        Fix = fix;
    }

    /// <summary>What the agent should do next, when there is a useful next step.</summary>
    public string? Fix { get; }
}
