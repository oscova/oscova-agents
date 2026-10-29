using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Oscova.Agents.Core.ApiContracts.OAuth;
using Oscova.Agents.Host.Constants;
using Oscova.Agents.Host.Services.Api;
using Oscova.Agents.Host.Services.UI;

namespace Oscova.Agents.Host.Components.Settings;

public class SettingsConnectedAccountsSectionBase : SettingsComponentBase
{
    [Inject] protected OAuthApiClient OAuthApiClient { get; set; } = default!;
    [Inject] protected NavigationManager NavigationManager { get; set; } = default!;
    [Inject] protected AuthenticationStateProvider AuthState { get; set; } = default!;

    protected OAuthStatusResponse _status = new();
    protected bool _isAdmin;
    private readonly HashSet<string> _disconnectingProviders = new(StringComparer.Ordinal);

    protected override async Task OnInitializedAsync()
    {
        _isAdmin = (await AuthState.GetAuthenticationStateAsync()).User.IsInRole("Admin");
        await LoadStatusAsync();
    }

    protected async Task LoadStatusAsync()
    {
        _status = await OAuthApiClient.GetStatusAsync();
    }

    protected void Connect(string provider)
    {
        NavigationManager.NavigateTo(ApiRoutes.OAuth.Authorize(provider), forceLoad: true);
    }

    protected async Task DisconnectAsync(string provider)
    {
        if (!_disconnectingProviders.Add(provider))
        {
            return;
        }

        StateHasChanged();

        try
        {
            await OAuthApiClient.DisconnectAsync(provider);
            await LoadStatusAsync();
            ToastService.Show($"{GetDisplayName(provider)} disconnected.", ToastType.Success);
        }
        catch (Exception ex)
        {
            ToastService.Show(ex.Message, ToastType.Error);
        }
        finally
        {
            _disconnectingProviders.Remove(provider);
            StateHasChanged();
        }
    }

    protected bool IsDisconnecting(string provider)
    {
        return _disconnectingProviders.Contains(provider);
    }

    private string GetDisplayName(string provider) =>
        _status.Providers.FirstOrDefault(entry => entry.Id == provider)?.DisplayName ?? "OAuth";
}

