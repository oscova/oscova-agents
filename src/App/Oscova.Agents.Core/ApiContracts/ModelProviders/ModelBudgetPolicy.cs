namespace Oscova.Agents.Core.ApiContracts.ModelProviders;

/// <summary>Stores optional estimated-spend limits for one owned model configuration.</summary>
public sealed record ModelBudgetPolicy(decimal? DailyLimitUsd = null, decimal? MonthlyLimitUsd = null, bool AlertsEnabled = false)
{
    /// <summary>Allows the settings reader to restore saved limits after a restart.</summary>
    public ModelBudgetPolicy() : this(null, null, false) { }
}
