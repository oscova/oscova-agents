namespace Oscova.Agents;

/// <summary>
/// Tracks the execution lifecycle of a tool call, independent of the LLM streaming state.
/// </summary>
public enum ToolExecutionState
{
    /// <summary>No execution state (default for non-tool segments).</summary>
    None,

    /// <summary>Tool call parsed from LLM output, awaiting execution.</summary>
    Pending,

    /// <summary>Tool is currently being invoked.</summary>
    Executing,

    /// <summary>Tool execution completed successfully.</summary>
    Completed,

    /// <summary>Tool execution failed.</summary>
    Failed,

    /// <summary>The run stopped before the tool call finished.</summary>
    Cancelled
}
