using System;

namespace Oscova.Agents.Hooks;

/// <summary>
/// Thrown when a blocking hook denies execution and the active run should stop cleanly.
/// </summary>
public sealed class HookDeniedException : Exception, IRunAbortException
{
    /// <summary>
    /// Creates a hook-denial exception with event and optional tool context.
    /// </summary>
    public HookDeniedException(HookEventKind eventKind, string? toolName, string message, string? reason)
        : base(message)
    {
        EventKind = eventKind;
        ToolName = toolName;
        Reason = reason;
    }

    /// <summary>
    /// Gets the blocking hook event that denied execution.
    /// </summary>
    public HookEventKind EventKind { get; }

    /// <summary>
    /// Gets the tool name when the denial occurred during tool invocation.
    /// </summary>
    public string? ToolName { get; }

    /// <summary>
    /// Gets the optional machine-readable denial reason.
    /// </summary>
    public string? Reason { get; }
}