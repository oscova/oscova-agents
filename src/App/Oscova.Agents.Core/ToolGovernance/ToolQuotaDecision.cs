namespace Oscova.Agents.Core.ToolGovernance;

/// <summary>
/// Result of attempting to reserve one tool call against a configured quota.
/// </summary>
public sealed record ToolQuotaDecision(
    bool IsAllowed,
    int Used,
    int MaxCalls,
    DateTimeOffset? ResetsAtUtc)
{
    public int Remaining => Math.Max(0, MaxCalls - Used);

    public static ToolQuotaDecision Allowed(int used, int maxCalls, DateTimeOffset? resetsAtUtc)
        => new(true, used, maxCalls, resetsAtUtc);

    public static ToolQuotaDecision Denied(int used, int maxCalls, DateTimeOffset? resetsAtUtc)
        => new(false, used, maxCalls, resetsAtUtc);
}
