using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Oscova.Agents.Infrastructure.Utils;
using Oscova.Agents.Core.ApiContracts.ModelProviders;
using Oscova.Agents.Core.ApiContracts.Dashboard;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Localization;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Llm;
using Oscova.Agents.Policies;
using Oscova.Agents.Core.Notifications;
using Oscova.Agents.Notifications;

namespace Oscova.Agents.Infrastructure.Metrics;

/// <summary>Checks completed spending once when a new model operation is admitted.</summary>
public sealed class ModelBudgetService(
    UserServiceManager users,
    IMetricsEventStore metrics,
    IModelProviderCatalog catalog,
    IServerTextLocalizer text,
    IServiceScopeFactory serviceScopes,
    NotificationHub<NotificationMessage> notifications,
    TimeProvider clock,
    ILogger<ModelBudgetService> logger) : IDisposable
{
    private readonly SemaphoreSlim _alertGate = new(1, 1);

    /// <summary>Releases notification serialization when the host stops.</summary>
    public void Dispose() => _alertGate.Dispose();

    /// <summary>Checks newly reached notification thresholds after completed usage has been saved.</summary>
    public async Task NotifyCompletedAsync(string userId, MetricsEvent usage)
    {
        try
        {
            foreach (var id in usage.Usage.Select(entry => entry.ProviderId).Distinct())
            {
                var scope = await users.GetOrCreateScopeAsync(userId).ConfigureAwait(false);
                if (scope.ModelProviderSettings.ModelProviderConfigs.Any(provider => provider.Id == id))
                {
                    await GetAsync(userId, id).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not complete model usage accounting for user {UserId}.", userId);
        }
    }
    /// <summary>Returns aggregate spending without exposing another consumer's activity.</summary>
    public async Task<ModelBudgetStatus> GetAsync(string userId, Guid providerId, CancellationToken ct = default,
        UsageLedger? pendingUsage = null)
    {
        var scope = await users.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        var settings = scope.ModelProviderSettings;
        await settings.RequireAccessAsync(providerId, ct).ConfigureAwait(false);
        var owner = settings.OwnerOf(providerId);
        var config = owner.OwnedProviders.First(provider => provider.Id == providerId);
        var ownerScope = await users.GetOrCreateScopeAsync(owner.UserId, ct).ConfigureAwait(false);
        var zone = ownerScope.UiSettings.GetTimeZone();
        var now = clock.GetUtcNow();
        var localDate = TimeZoneInfo.ConvertTime(now, zone).Date;
        var month = new DateTime(localDate.Year, localDate.Month, 1);
        DateTimeOffset Utc(DateTime date) => new(TimeZoneInfo.ConvertTimeToUtc(date, zone));
        var dayStart = Utc(localDate);
        var monthStart = Utc(month);
        var provider = catalog.Find(config.Type);
        var isLocal = EgressClassifier.Classify(config.Type, config.BaseUrl, provider?.DefaultEndpoint,
            config.IsLocalEndpoint, provider?.DefaultIsLocalEndpoint ?? false) != EgressClass.Cloud;
        decimal daily = 0, monthly = 0;
        var unpriced = false;
        var available = true;
        try
        {
            // Shared configurations have one pool across the existing per-consumer metrics files.
            var consumers = ownerScope.IsInRole("Admin") && Directory.Exists(PathUtil.UsersPath)
                ? Directory.EnumerateDirectories(PathUtil.UsersPath).Select(Path.GetFileName)
                : [owner.UserId];
            foreach (var consumer in consumers)
            {
                if (string.IsNullOrWhiteSpace(consumer)) { continue; }

                foreach (var record in await metrics.ReadAsync(consumer, monthStart, ct).ConfigureAwait(false))
                {
                    if (record.Timestamp > now) { continue; }

                    foreach (var usage in record.Usage.Where(usage => usage.ProviderId == providerId))
                    {
                        unpriced |= usage.HasMissingUsage || usage.InputTokens > 0 && usage.InputCostPerMillionTokens is null ||
                            usage.OutputTokens > 0 && usage.OutputCostPerMillionTokens is null;
                        var cost = CostOf(usage);
                        monthly += cost;
                        if (record.Timestamp >= dayStart) { daily += cost; }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            available = false;
            logger.LogWarning(ex, "Budget accounting unavailable for provider {ProviderId} owned by {OwnerId}.", providerId, owner.UserId);
        }

        // Goal boundaries include completed calls not yet flushed by the outer run's telemetry.
        foreach (var usage in pendingUsage?.Snapshot().Where(usage => usage.ProviderId == providerId) ?? [])
        {
            var cost = CostOf(usage);
            daily += cost;
            monthly += cost;
            unpriced |= usage.HasMissingUsage || usage.InputTokens > 0 && usage.InputCostPerMillionTokens is null
                || usage.OutputTokens > 0 && usage.OutputCostPerMillionTokens is null;
        }

        var status = new ModelBudgetStatus(providerId, config.Name, owner.Budgets.GetValueOrDefault(providerId) ?? new(),
            daily, monthly, TimeZoneInfo.ConvertTime(Utc(localDate.AddDays(1)), zone), TimeZoneInfo.ConvertTime(Utc(month.AddMonths(1)), zone),
            owner.UserId == userId, isLocal, !isLocal && unpriced, available);
        await NotifyThresholdAsync(ownerScope, status, ct).ConfigureAwait(false);
        return status;
    }

    private static decimal CostOf(AgentProviderUsage usage) =>
        (usage.InputTokens * (usage.InputCostPerMillionTokens ?? 0)
            + usage.OutputTokens * (usage.OutputCostPerMillionTokens ?? 0)) / 1_000_000m;

    /// <summary>Reports skipped queued work through the existing notification inbox.</summary>
    public async Task NotifyDeniedAsync(string userId, Guid providerId, CancellationToken ct = default)
    {
        // The inbox file is plaintext, so it names the provider id; readers show the label.
        var status = await GetAsync(userId, providerId, ct).ConfigureAwait(false) with { ProviderName = providerId.ToString() };
        await notifications.NotifyAsync(userId, new NotificationMessage(status.ProviderName,
            await NoticeAsync(userId, status, ct).ConfigureAwait(false), NotificationKind.Generic, UserId: userId)).ConfigureAwait(false);
    }

    private async Task NotifyThresholdAsync(UserServiceScope owner, ModelBudgetStatus status, CancellationToken ct)
    {
        if (!status.Policy.AlertsEnabled || status.IsLocal || !status.AccountingAvailable) { return; }

        await _alertGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var periods = new[]
            {
                ("daily", status.Policy.DailyLimitUsd, status.DailySpentUsd, status.DailyReset),
                ("monthly", status.Policy.MonthlyLimitUsd, status.MonthlySpentUsd, status.MonthlyReset)
            };
            foreach (var (period, limit, spent, reset) in periods)
            {
                var key = $"{status.ProviderId:N}/{period}";
                if (limit is not > 0 || spent < limit * 0.8m ||
                    owner.ModelProviderSettings.BudgetAlertPeriods.TryGetValue(key, out var notifiedReset) && notifiedReset == reset) { continue; }
                // Persist the claim before delivery so a restart or cap edit cannot resend this period's alert.
                owner.ModelProviderSettings.BudgetAlertPeriods[key] = reset;
                await owner.ModelProviderSettings.SaveAsync().ConfigureAwait(false);
                var message = text.Localize(owner.UiSettings.CultureName, NotificationTextKeys.ModelBudgetThreshold,
                    "{0} has reached at least 80% of a spending limit. Open Models & Providers to review its budget.", status.ProviderId.ToString());
                await notifications.NotifyAsync(owner.UserId,
                    new NotificationMessage(status.ProviderId.ToString(), message, NotificationKind.Generic, UserId: owner.UserId)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not deliver a budget notification for provider {ProviderId}.", status.ProviderId);
        }
        finally { _alertGate.Release(); }
    }

    /// <summary>Explains exhaustion in the consuming account's configured language.</summary>
    public async Task<string> NoticeAsync(string userId, ModelBudgetStatus status, CancellationToken ct = default)
    {
        var scope = await users.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        return text.Localize(scope.UiSettings.CultureName,
            status.CanOverride ? NotificationTextKeys.ModelBudgetOwnerExceeded : NotificationTextKeys.ModelBudgetSharedExceeded,
            status.CanOverride ? "The budget for {0} has been reached. You can continue this operation anyway."
                : "The budget for {0} has been reached. Contact your operator or choose another model provider.",
            status.ProviderName);
    }

    /// <summary>Explains an embedding pause without reporting an empty search or a provider failure.</summary>
    public async Task<string?> EmbeddingPauseAsync(string userId, Guid providerId, CancellationToken ct = default)
    {
        if (!(await GetAsync(userId, providerId, ct).ConfigureAwait(false)).IsExceeded) { return null; }

        var scope = await users.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        return text.Localize(scope.UiSettings.CultureName, NotificationTextKeys.ModelBudgetSearchPaused,
            "Semantic search and indexing are paused because the embedding budget has been reached. Try again after the budget resets or its owner changes the limit.");
    }

    /// <summary>Uses the existing authenticated confirmation route for one owner-approved operation.</summary>
    public async Task<bool> AdmitAsync(string userId, Guid providerId, ElicitationContext? context = null, CancellationToken ct = default)
    {
        var status = await GetAsync(userId, providerId, ct).ConfigureAwait(false);
        if (!status.IsExceeded) { return true; }

        if (!status.CanOverride || context is null || context.UserId != userId) { return false; }

        var scope = await users.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        var label = text.Localize(scope.UiSettings.CultureName, NotificationTextKeys.ModelBudgetContinue, "Continue anyway");
        await using var serviceScope = serviceScopes.CreateAsyncScope();
        var response = await serviceScope.ServiceProvider.GetRequiredService<IElicitationRouter>().RouteElicitationAsync(new ElicitRequestParams
        {
            Message = await NoticeAsync(userId, status, ct).ConfigureAwait(false),
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties = { ["continue"] = new ElicitRequestParams.BooleanSchema { Title = label } },
                Required = ["continue"]
            }
        }, context, ct).ConfigureAwait(false);
        return response.Action == "accept" && response.Content?.TryGetValue("continue", out var answer) == true &&
            answer.ValueKind == JsonValueKind.True;
    }
}
