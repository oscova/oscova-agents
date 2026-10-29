using Oscova.Agents.Core.ApiContracts.ModelProviders;
using Oscova.Agents.Host.Components.Dialogs;
using Oscova.Agents.Host.Services.UI;

namespace Oscova.Agents.Host.Components.Chat;

public partial class ChatPanel
{
    private ModelBudgetStatus? _budgetStatus;
    private string? _budgetSelectionKey;

    // A denial can name the effective fallback provider while the composer still selects the default.
    private ModelBudgetStatus? CurrentBudget => _budgetSelectionKey == $"{ChatThread.Id}|{ComposerProvider?.Id}" ? _budgetStatus : null;

    /// <summary>Loads spending independently of the context meter, including an empty composer.</summary>
    private async Task RefreshBudgetAsync()
    {
        var providerId = ComposerProvider?.Id;
        _budgetStatus = null;
        _budgetSelectionKey = $"{ChatThread.Id}|{providerId}";
        if (providerId is null) { return; }

        try
        {
            var statuses = await ChatApiClient.GetModelBudgetsAsync();
            if (!_isDisposed && ComposerProvider?.Id == providerId)
            {
                _budgetStatus = statuses.FirstOrDefault(status => status.ProviderId == providerId);
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not load composer budget for provider {ProviderId}.", providerId);
        }
    }

    /// <summary>Confirms one pending request without altering the saved provider limit.</summary>
    private async Task<bool> ConfirmBudgetAsync(ModelBudgetStatus status)
    {
        _budgetStatus = status;
        await InvokeAsync(StateHasChanged);
        if (!status.CanOverride) { return false; }

        var result = await DialogService.ShowAsync<ModelBudgetDialog>(new Dictionary<string, object?> { ["Status"] = status });
        _restoreComposerFocus = result != DialogResult.Ok;
        return result == DialogResult.Ok;
    }
}
