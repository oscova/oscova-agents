using Oscova.Agents.Conversations;
using Oscova.Agents.Core.Conversation;

namespace Oscova.Agents.Core.Hooks;

/// <summary>
/// Converts runtime execution kinds to the stable names used in external hook envelopes.
/// </summary>
public static class HookExecutionKinds
{
    public const string WebChat = ChatChannels.Web;
    public const string ChannelChat = ChatChannels.Channel;
    public const string TaskRun = "task_run";
    public const string Unknown = "unknown";

    /// <summary>
    /// Returns the stable external hook name for a runtime execution kind.
    /// </summary>
    public static string FromRunExecutionKind(RunExecutionKind kind)
    {
        return kind switch
        {
            RunExecutionKind.WebChat => WebChat,
            RunExecutionKind.ChannelChat => ChannelChat,
            RunExecutionKind.TaskRun => TaskRun,
            _ => Unknown
        };
    }
}
