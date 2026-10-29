using Oscova.Agents.Core.Notifications;
using Oscova.Agents.Core.ApiContracts.Updates;
using Oscova.Agents.Host.Components.Dialogs;
using Oscova.Agents.Host.Services.Api;
using Oscova.Agents.Host.Services.UI;
using Microsoft.AspNetCore.Components;

namespace Oscova.Agents.Host.Components.Shell;

/// <summary>
/// Builds notification deep links so bell and inbox surfaces route task notifications consistently.
/// </summary>
public static class NotificationNavigation
{
    /// <summary>Opens fresh component status from either saved notification surface.</summary>
    public static async Task<bool> OpenUpdateAsync(NotificationEntry entry, IDialogService dialogs, ManagementApiClient api, NavigationManager navigation)
    {
        if (entry.Kind == NotificationKind.AvatarUpdate)
        {
            await dialogs.ShowAsync<AvatarUpdateDialog>();
            return true;
        }

        if (entry.Kind == NotificationKind.HarnessUpdate)
        {
            var status = await api.GetApplicationUpdateStatusAsync(true);
            var result = await dialogs.ShowAsync<ApplicationUpdateDialog>(new Dictionary<string, object?>
            {
                [nameof(ApplicationUpdateDialog.InitialStatus)] = status,
                [nameof(ApplicationUpdateDialog.StatusChanged)] = EventCallback.Factory.Create<ApplicationUpdateStatus>(api, value => status = value)
            });
            if (status is { State: "Succeeded", OperationId: not null })
            {
                await api.AcknowledgeApplicationUpdateAsync(status.OperationId);
                if (result == DialogResult.Ok) { navigation.NavigateTo("/settings/agents-ai/plugins"); }
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Builds the tasks page URL for a task notification, preserving run metadata when present.
    /// </summary>
    public static string BuildTaskUrl(string taskId, string? runId)
    {
        var url = $"/tasks?taskId={Uri.EscapeDataString(taskId)}";
        if (!string.IsNullOrWhiteSpace(runId))
        {
            url += $"&runId={Uri.EscapeDataString(runId)}";
        }

        return url;
    }
}
