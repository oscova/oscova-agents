using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Oscova.Agents.Core.ApiContracts.Achievements;
using Oscova.Agents.Host.Localization;
using Oscova.Agents.Host.Services.Api;
using Oscova.Agents.Infrastructure.Users;

namespace Oscova.Agents.Host.Components.Pages;

/// <summary>
/// Displays the signed-in user's unlocked achievements and derived level.
/// </summary>
public partial class AchievementsPage : ComponentBase
{
    [Inject] private ManagementApiClient ManagementApiClient { get; set; } = default!;
    [Inject] private IStringLocalizer<AchievementText> AchievementLoc { get; set; } = default!;
    [Inject] private UserServiceScope UserScope { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    private AchievementSummary? _summary;
    private bool _isLoading = true;
    private string? _error;

    protected override async Task OnInitializedAsync()
    {
        if (!UserScope.UiSettings.IsGamificationEnabled)
        {
            NavigationManager.NavigateTo("/");
            return;
        }

        try
        {
            _summary = await ManagementApiClient.GetAchievementSummaryAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _isLoading = false;
        }
    }

    private string FormatAchievementText(string key, string fallback, params object?[] args)
    {
        return string.Format(CultureInfo.CurrentCulture, AchievementLoc.GetOrDefault(key, fallback), args);
    }
}
