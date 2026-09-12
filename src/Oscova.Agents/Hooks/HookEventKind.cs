namespace Oscova.Agents.Hooks;

/// <summary>
/// Identifies lifecycle events that external hooks can observe or block.
/// </summary>
public enum HookEventKind
{
    /// <summary>
    /// A scheduled or externally triggered task is about to start.
    /// </summary>
    TaskStart = 0,

    /// <summary>
    /// A task completed successfully.
    /// </summary>
    TaskComplete = 1,

    /// <summary>
    /// A task finished with a failure.
    /// </summary>
    TaskFailed = 2,

    /// <summary>
    /// A new agent conversation session is starting.
    /// </summary>
    SessionStart = 3,

    /// <summary>
    /// A user prompt was submitted before model execution.
    /// </summary>
    UserPromptSubmit = 4,

    /// <summary>
    /// An agent is about to call a tool.
    /// </summary>
    PreToolUse = 5,

    /// <summary>
    /// An agent has finished calling a tool.
    /// </summary>
    PostToolUse = 6
}