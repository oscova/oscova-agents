using Oscova.Agents.Notifications;

namespace Oscova.Agents.Core.Notifications;

/// <summary>
/// Represents a live notification payload delivered through the in-process notification hub.
/// </summary>
public sealed record NotificationMessage(
    string Title,
    string Body,
    NotificationKind Kind,
    string? UserId = null,
    string? AgentId = null,
    string? TaskId = null,
    string? RunId = null,
    string? ThreadId = null,
    string? PluginId = null,
    NotificationPriority Priority = NotificationPriority.Normal);

/// <summary>
/// Describes the UI and routing category for an in-app notification.
/// </summary>
public enum NotificationKind
{
    Generic,
    TaskSuccess,
    TaskFailure,
    SystemError,
    SystemWarning,
    ChatMessage,
    PluginSetup,
    HarnessUpdate,
    AvatarUpdate
}
