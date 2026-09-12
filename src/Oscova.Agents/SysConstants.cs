namespace Oscova.Agents;

/// <summary>
/// Defines reserved system names used by framework-owned agents, tags, and tools.
/// </summary>
public static class SysConstants
{
    /// <summary>
    /// Prefix applied to framework-owned identifiers so they do not collide with user tools.
    /// </summary>
    public const string Prefix = "sys_";

    /// <summary>
    /// Reserved identifier for the user's main agent.
    /// </summary>
    public const string MainAgentName = Prefix + "main_agent";

    /// <summary>
    /// Reserved identifier used when a generic framework agent name is needed.
    /// </summary>
    public const string DefaultAgentName = Prefix + "agent";

    /// <summary>
    /// Reserved XML-like instruction tag name used in agent prompts.
    /// </summary>
    public const string AgentInstructionTag = Prefix + "agent_instruction";

    /// <summary>
    /// Name of the tool function used by MCP services to provide notifications.
    /// </summary>
    public const string ServiceNotificationToolName = Prefix + "get_notification";

    /// <summary>
    /// Name of the tool function used to send proactive notifications to the user.
    /// </summary>
    public const string NotifyUserToolName = Prefix + "notify_user";

    /// <summary>
    /// A valid notification type that indicates the payload is a service notification.
    /// </summary>
    public const string ValidNotificationType = Prefix + "notification";

    /// <summary>
    /// Reserved identifier for the agent that handles proactive notifications.
    /// </summary>
    public const string NotificationAgentName = Prefix + "notification_agent";
}