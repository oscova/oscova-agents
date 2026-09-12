using System;
using System.Collections.Generic;
using System.Linq;

namespace Oscova.Agents.Policies;

/// <summary>
/// Resolves sparse per-agent tool policy overrides by qualified tool name.
/// </summary>
public sealed class ToolPolicyResolver
{
    private readonly Dictionary<string, AgentToolPolicy> _policies;

    /// <summary>
    /// Creates a resolver from sparse policy overrides, keeping the last duplicate entry by tool name.
    /// </summary>
    /// <param name="policies">The per-agent policy overrides to index.</param>
    public ToolPolicyResolver(IEnumerable<AgentToolPolicy>? policies)
    {
        _policies = policies?
            .Where(policy => !string.IsNullOrWhiteSpace(policy.ToolName))
            .GroupBy(policy => policy.ToolName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Last(),
                StringComparer.OrdinalIgnoreCase)
            ?? [];
    }

    /// <summary>
    /// Gets the explicit override for a tool.
    /// </summary>
    /// <param name="toolName">The qualified tool name.</param>
    /// <returns>The stored override, or <see cref="ToolApprovalMode.Default"/> when no override exists.</returns>
    public ToolApprovalMode GetApprovalMode(string toolName)
    {
        return _policies.TryGetValue(toolName, out var policy)
            ? policy.ApprovalMode
            : ToolApprovalMode.Default;
    }

    /// <summary>
    /// Resolves a tool's effective approval mode from an agent override and a reflected default.
    /// </summary>
    /// <param name="toolName">The qualified tool name.</param>
    /// <param name="defaultApprovalMode">The tool method's default approval mode.</param>
    /// <returns>The agent override when present; otherwise the supplied default.</returns>
    public ToolApprovalMode GetEffectiveApprovalMode(
        string toolName,
        ToolApprovalMode defaultApprovalMode)
    {
        var overrideMode = GetApprovalMode(toolName);
        return overrideMode == ToolApprovalMode.Default
            ? defaultApprovalMode
            : overrideMode;
    }

    /// <summary>
    /// Returns true when a policy entry represents a non-default override worth persisting.
    /// </summary>
    /// <param name="policy">The policy entry to evaluate.</param>
    /// <returns><see langword="true"/> when the entry has a tool name and non-default approval mode.</returns>
    public static bool IsPersistedOverride(AgentToolPolicy policy)
    {
        return !string.IsNullOrWhiteSpace(policy.ToolName) &&
               policy.ApprovalMode != ToolApprovalMode.Default;
    }
}
