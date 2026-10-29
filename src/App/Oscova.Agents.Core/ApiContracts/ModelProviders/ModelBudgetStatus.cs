namespace Oscova.Agents.Core.ApiContracts.ModelProviders;

/// <summary>Exposes aggregate spending without revealing other consumers or provider credentials.</summary>
public sealed record ModelBudgetStatus(
    Guid ProviderId, string ProviderName, ModelBudgetPolicy Policy,
    decimal DailySpentUsd, decimal MonthlySpentUsd, DateTimeOffset DailyReset, DateTimeOffset MonthlyReset,
    bool CanOverride, bool IsLocal, bool HasUnpricedUsage, bool AccountingAvailable)
{
    public bool IsExceeded { get; init; } = !IsLocal && AccountingAvailable &&
        (Policy.DailyLimitUsd is > 0 && DailySpentUsd >= Policy.DailyLimitUsd ||
         Policy.MonthlyLimitUsd is > 0 && MonthlySpentUsd >= Policy.MonthlyLimitUsd);

    public DateTimeOffset? AvailableAt { get; init; } = !IsLocal && AccountingAvailable
        ? new DateTimeOffset?[]
        {
            Policy.DailyLimitUsd is > 0 && DailySpentUsd >= Policy.DailyLimitUsd ? DailyReset : null,
            Policy.MonthlyLimitUsd is > 0 && MonthlySpentUsd >= Policy.MonthlyLimitUsd ? MonthlyReset : null
        }.Max()
        : null;

    /// <summary>Preserves admission and recovery information while withholding another owner's finances.</summary>
    public ModelBudgetStatus ForViewer() => CanOverride ? this : this with
    {
        Policy = new(), DailySpentUsd = 0, MonthlySpentUsd = 0,
        DailyReset = default, MonthlyReset = default, HasUnpricedUsage = false
    };
}
