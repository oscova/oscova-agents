using Microsoft.Extensions.Localization;
using Oscova.Agents.Host.Localization;
using Oscova.Agents.Tasks;

namespace Oscova.Agents.Host.Components.Tasks;

/// <summary>Keeps task state labels consistent between the directory and details.</summary>
internal static class AgentTaskStatusDisplay
{
    /// <summary>Uses the same translated run state in the directory and run dialogs.</summary>
    internal static string GetRunText(AgentTaskStatus status, IStringLocalizer<CommonText> loc)
    {
        return status switch
        {
            AgentTaskStatus.Completed => loc.GetOrDefault(CommonTextKeys.Completed, "Completed"),
            AgentTaskStatus.Running => loc.GetOrDefault(CommonTextKeys.TaskStatusRunning, "Running"),
            AgentTaskStatus.Failed => loc.GetOrDefault(CommonTextKeys.TaskPageFailedBadge, "Failed"),
            AgentTaskStatus.Pending => loc.GetOrDefault(CommonTextKeys.TaskStatusPending, "Pending"),
            AgentTaskStatus.Skipped => loc.GetOrDefault(CommonTextKeys.TaskStatusSkipped, "Skipped"),
            _ => loc.GetOrDefault(CommonTextKeys.Unknown, "Unknown")
        };
    }

    /// <summary>Applies orphan and paused state before the latest run's status.</summary>
    internal static (string Text, string BadgeClass) Get(
        AgentTaskDefinition task, AgentTaskStatus? status, bool isOrphan, IStringLocalizer<CommonText> loc)
    {
        if (isOrphan) { return (loc.GetOrDefault(CommonTextKeys.TaskStatusOrphan, "Orphan"), "badge-warning"); }

        if (!task.IsEnabled) { return (loc.GetOrDefault(CommonTextKeys.TaskStatusPaused, "Paused"), "badge-neutral"); }

        return status switch
        {
            AgentTaskStatus.Running => (loc.GetOrDefault(CommonTextKeys.TaskStatusRunning, "Running"), "badge-warning"),
            AgentTaskStatus.Failed => (loc.GetOrDefault(CommonTextKeys.TaskStatusNeedsAttention, "Needs attention"), "badge-error"),
            AgentTaskStatus.Completed => (loc.GetOrDefault(CommonTextKeys.TaskStatusHealthy, "Healthy"), "badge-success"),
            AgentTaskStatus.Pending => (loc.GetOrDefault(CommonTextKeys.TaskStatusPending, "Pending"), "badge-neutral"),
            _ => (loc.GetOrDefault(CommonTextKeys.TaskStatusReady, "Ready"), "badge-primary")
        };
    }
}
