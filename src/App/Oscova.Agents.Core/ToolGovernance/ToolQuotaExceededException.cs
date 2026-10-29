using Oscova.Agents.Hooks;

namespace Oscova.Agents.Core.ToolGovernance;

/// <summary>
/// Raised when a configured per-tool call budget is exhausted before invocation.
/// </summary>
public sealed class ToolQuotaExceededException : InvalidOperationException, IRunAbortException
{
    public ToolQuotaExceededException(string toolName, ToolQuotaDecision decision)
        : base(BuildMessage(toolName, decision))
    {
        ToolName = toolName;
        Decision = decision;
    }

    public string ToolName { get; }

    public ToolQuotaDecision Decision { get; }

    private static string BuildMessage(string toolName, ToolQuotaDecision decision)
    {
        var resetText = decision.ResetsAtUtc is null
            ? "after the current quota window resets"
            : $"at {decision.ResetsAtUtc.Value:yyyy-MM-dd HH:mm} UTC";

        return $"Tool '{toolName}' has reached its limit of {decision.MaxCalls} calls per 24 hours. It resets {resetText}.";
    }
}