using System;
using Oscova.Agents.Notifications;

namespace Oscova.Agents.Tasks;

/// <summary>Reports a skipped task run with its reason and notification priority.</summary>
public sealed class AgentTaskSkippedException(
    string messageKey,
    string fallbackMessage,
    NotificationPriority priority = NotificationPriority.Normal) : Exception(fallbackMessage)
{
    /// <summary>Identifies the localized reason for skipping the run.</summary>
    public string MessageKey { get; } = messageKey;
    /// <summary>Indicates how prominently the skip notification should be announced.</summary>
    public NotificationPriority Priority { get; } = priority;
}
