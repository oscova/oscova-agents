using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Oscova.Agents.Core.ApiContracts.Credentials;
using Oscova.Agents.Host.Components.Dialogs;
using Oscova.Agents.Host.Components.Dialogs.Settings;
using Oscova.Agents.Host.Localization;
using Oscova.Agents.Host.Services.Api;
using Oscova.Agents.Host.Services.UI;

namespace Oscova.Agents.Host.Components.Settings;

public partial class SettingsCredentialsSectionBase : SettingsComponentBase
{
    [Inject] protected SettingsApiClient SettingsApiClient { get; set; } = default!;
    [Inject] protected IDialogService DialogService { get; set; } = default!;
    [Inject] protected IStringLocalizer<SettingsText> Loc { get; set; } = default!;
    [Inject] protected IStringLocalizer<CommonText> CommonLoc { get; set; } = default!;

    protected CredentialsContract _view = new();

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
    }

    protected async Task OpenCreateDialogAsync()
    {
        var result = await DialogService.ShowAsync<CredentialEditorDialog>();
        if (result == DialogResult.Ok)
        {
            await LoadAsync();
        }
    }

    protected async Task OpenEditDialogAsync(string credentialKey, bool isFreeForm)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["CredentialKey"] = credentialKey,
            ["IsFreeForm"] = isFreeForm
        };

        var result = await DialogService.ShowAsync<CredentialEditorDialog>(parameters);
        if (result == DialogResult.Ok)
        {
            await LoadAsync();
        }
    }

    protected async Task DeleteAsync(string credentialKey, string alias)
    {
        var result = await DialogService.ShowAsync<ConfirmDialog>(
            Loc.GetOrDefault(SettingsTextKeys.CredentialsDeleteConfirmTitle, "Delete credential"),
            string.Format(
                CultureInfo.CurrentCulture,
                Loc.GetOrDefault(SettingsTextKeys.CredentialsDeleteConfirmMessage, "Are you sure you want to delete '{0}'?"),
                alias));

        if (result != DialogResult.Ok)
        {
            return;
        }

        try
        {
            var conflict = await SettingsApiClient.DeleteCredentialAsync(credentialKey);
            if (conflict?.AffectedSettings.Count > 0)
            {
                ToastService.Show(
                    string.Format(
                        CultureInfo.CurrentCulture,
                        Loc.GetOrDefault(SettingsTextKeys.CredentialsStillLinkedWarning, "This credential is still linked to {0}. Remove those references before deleting it."),
                        string.Join(", ", conflict.AffectedSettings)),
                    ToastType.Warning);
                return;
            }

            await LoadAsync();

            ToastService.Show(Loc.GetOrDefault(SettingsTextKeys.CredentialsDeletedToast, "Credential deleted."), ToastType.Success);
        }
        catch (Exception ex)
        {
            ToastService.Show(ex.Message, ToastType.Error);
        }
    }

    /// <summary>True when the slot has a stored row and every required field has a usable value.</summary>
    protected static bool IsConfigured(CredentialSlotContract slot)
    {
        if (!slot.RowExists)
        {
            return false;
        }

        var requiredFields = slot.Fields.Where(field => field.IsRequired).ToList();
        return requiredFields.Count == 0
            ? slot.Fields.Any(field => field.IsSet)
            : requiredFields.All(field => field.IsSet);
    }

    private async Task LoadAsync()
    {
        _view = await SettingsApiClient.GetCredentialsViewAsync();
    }
}
