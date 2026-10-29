using Oscova.Agents.Core.ApiContracts.Chat;

namespace Oscova.Agents.Host.Components.Prompts;

/// <summary>
/// Represents the user's decision in response to a tool approval request.
/// </summary>
public readonly record struct ToolApprovalChoice(string RequestId, ToolApprovalDecision Decision);
