using Oscova.Agents.Core.Hardware;
using Oscova.Agents.Core.Localization;
using Oscova.Agents.Core.ApiContracts.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oscova.Agents.Conversations;
using Oscova.Agents.Core.ApiContracts.Settings;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Host.Server.Services;
using Oscova.Agents.Host.Server.Chat;

namespace Oscova.Agents.Host.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/settings")]
[IgnoreAntiforgeryToken]
public sealed class SettingsController(
    ChatSessionService chatSessionService,
    ThreadRunGuard threadRunGuard,
    ChatRunProjectionStore projectionStore,
    UserServiceManager userServiceManager,
    IHostPowerService hostPowerService,
    IServerTextLocalizer textLocalizer)
    : UserScopeControllerBase(userServiceManager)
{
    [HttpGet("ui")]
    public async Task<ActionResult<UiSettingsSummary>> GetUiSettings(CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);
        var settings = userScope.UiSettings;

        return Ok(new UiSettingsSummary
        {
            Theme = settings.Theme,
            TimeZoneId = settings.TimeZoneId,
            HideUserNameBadge = settings.HideUserNameBadge,
            IsGamificationEnabled = settings.IsGamificationEnabled,
            IsChatTitleGenerationEnabled = settings.IsChatTitleGenerationEnabled,
            AreNotificationsMuted = settings.AreNotificationsMuted,
            ChatFollowUpMode = settings.ChatFollowUpMode,
            ShowLogWarningNotifications = settings.ShowLogWarningNotifications,
            ShowSystemErrorNotifications = settings.ShowSystemErrorNotifications,
            CultureName = settings.CultureName,
            ChatResumeWindowMinutes = settings.ChatResumeWindowMinutes,
            IsLowBatteryTaskProtectionEnabled = settings.IsLowBatteryTaskProtectionEnabled,
            LowBatteryTaskThresholdPercentage = settings.LowBatteryTaskThresholdPercentage,
            HostPower = hostPowerService.GetSnapshot(),
            ConversationCount = userScope.AppService.Threads.Count
        });
    }

    [HttpPut("ui")]
    public async Task<IActionResult> SaveUiSettings([FromBody] SaveUiSettingsRequest request, CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);
        var settings = userScope.UiSettings;

        // A fresh server read prevents a stale settings page from enabling unsupported protection.
        if (request.IsLowBatteryTaskProtectionEnabled && !settings.IsLowBatteryTaskProtectionEnabled &&
            !hostPowerService.GetSnapshot().HasUsableBatteryInformation)
        {
            return BadRequest(new ErrorResult
            {
                Message = textLocalizer.Localize(settings.CultureName,
                    NotificationTextKeys.TaskBatteryInformationUnavailable,
                    "Battery or power connection information is unavailable on the computer running Oscova.")
            });
        }

        settings.Theme = string.IsNullOrWhiteSpace(request.Theme)
            ? AssistantUiSettings.DefaultTheme
            : request.Theme.Trim();
        settings.TimeZoneId = request.TimeZoneId?.Trim() ?? string.Empty;
        settings.HideUserNameBadge = request.HideUserNameBadge;
        settings.IsGamificationEnabled = request.IsGamificationEnabled;
        settings.IsChatTitleGenerationEnabled = request.IsChatTitleGenerationEnabled;
        settings.AreNotificationsMuted = request.AreNotificationsMuted;
        settings.ChatFollowUpMode = request.ChatFollowUpMode;
        settings.ShowLogWarningNotifications = request.ShowLogWarningNotifications;
        settings.ShowSystemErrorNotifications = request.ShowSystemErrorNotifications;
        settings.CultureName = UiCultureCatalog.NormalizeOrDefault(request.CultureName);
        settings.ChatResumeWindowMinutes = AssistantUiSettings.NormalizeChatResumeWindowMinutes(request.ChatResumeWindowMinutes);

        settings.IsLowBatteryTaskProtectionEnabled = request.IsLowBatteryTaskProtectionEnabled;
        settings.LowBatteryTaskThresholdPercentage = request.LowBatteryTaskThresholdPercentage;

        await settings.SaveAsync();
        return NoContent();
    }

    [HttpDelete("conversations")]
    public async Task<IActionResult> DeleteAllConversations(CancellationToken ct)
    {
        var userId = GetUserId();
        using var deletionLease = await threadRunGuard.CancelAndAcquireUserAsync(userId, ct);
        await chatSessionService.DeleteAllThreadsAsync(userId, ct);
        projectionStore.RemoveUser(userId);
        return NoContent();
    }
}
