namespace Oscova.Agents.Notifications;

/// <summary>Expresses how prominently a notification should be announced.</summary>
public enum NotificationPriority
{
    /// <summary>Uses the host's ordinary notification presentation.</summary>
    Normal,
    /// <summary>Requests less prominent presentation while retaining the notification.</summary>
    Low
}
