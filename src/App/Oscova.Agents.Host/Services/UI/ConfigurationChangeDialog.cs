using Microsoft.Extensions.Localization;
using Oscova.Agents.Host.Components.Dialogs;
using Oscova.Agents.Host.Localization;

namespace Oscova.Agents.Host.Services.UI;

/// <summary>Uses the same interruption confirmation for shared configuration editors.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "MA0004", Justification = "Dialog operations require the Blazor synchronization context.")]
internal static class ConfigurationChangeDialog
{
    /// <summary>Applies a change, asking once when the server reports affected active work.</summary>
    public static async Task<bool> ApplyAsync(IDialogService dialogs, IStringLocalizer<CommonText> text, Func<bool, Task<bool>> apply)
    {
        if (await apply(false)) { return true; }

        var result = await dialogs.ShowAsync<ConfirmDialog>(
            text.GetOrDefault(CommonTextKeys.Settings, "Settings"),
            text.GetOrDefault(CommonTextKeys.GovernanceConfirmInterruption, "This change will stop work using this resource. Apply it now?"));
        if (result != DialogResult.Ok) { return false; }

        if (!await apply(true)) { throw new InvalidOperationException("The confirmed change could not be applied. Refresh the settings and try again."); }

        return true;
    }
}
