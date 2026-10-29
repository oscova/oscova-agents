namespace Oscova.Agents.Core.ApiContracts.Tools;

/// <summary>
/// Current usage for a tool quota window.
/// </summary>
public sealed class ToolQuotaUsageSummary
{
    public int Used { get; set; }
    public int MaxCalls { get; set; }
    public int Remaining { get; set; }
    public DateTimeOffset? WindowStartedAtUtc { get; set; }
    public DateTimeOffset? ResetsAtUtc { get; set; }
}
