namespace Oscova.Agents.Core.Conversation;

/// <summary>
/// Identifies which app execution path owns the current live agent run.
/// </summary>
public enum RunExecutionKind
{
    WebChat,
    ChannelChat,
    TaskRun
}
