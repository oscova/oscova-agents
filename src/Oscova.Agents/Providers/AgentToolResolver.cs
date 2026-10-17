using System.Collections.Generic;
using Microsoft.Extensions.AI;
using Oscova.Agents.Collections;
using Oscova.Agents.Policies;

namespace Oscova.Agents.Providers;

/// <summary>Resolves allowed tool names with egress and approval policy for direct and search-based exposure.</summary>
public sealed class AgentToolResolver(
    AgentToolCollection tools,
    IEnumerable<AgentToolPolicy>? toolPolicies = null,
    bool airGapped = false,
    bool suppressApprovalRequiredTools = false)
{
    private readonly ToolPolicyResolver _policyResolver = new(toolPolicies);

    /// <summary>Resolves one allowed tool name to its exposable form, or null when it must not be exposed.</summary>
    public AITool? Resolve(string toolName)
    {
        var tool = tools.Find(toolName);
        if (tool is null)
        {
            return null;
        }


        if (airGapped && tools.GetNetworkScope(toolName) == NetworkScope.Egress)
        {
            return null;
        }

        var mode = _policyResolver.GetEffectiveApprovalMode(
            toolName,
            tools.GetDefaultApprovalMode(toolName));

        return mode switch
        {
            // Sub-agent (agent-as-tool) runs never reach ToolApprovalRunLoop, so an approval prompt
            // here would never be routed; drop the tool rather than expose an unanswerable one.
            ToolApprovalMode.RequireApproval => suppressApprovalRequiredTools ? null : new ApprovalRequiredAIFunction(tool),
            ToolApprovalMode.Deny => null,
            _ => tool
        };
    }

    /// <summary>Resolves all allowed names to exposable tools, dropping any denied or unavailable tools.</summary>
    public IReadOnlyList<AITool> ResolveAll(IEnumerable<string> allowedToolNames)
    {
        var resolved = new List<AITool>();
        foreach (var name in allowedToolNames)
        {
            var tool = Resolve(name);
            if (tool is not null)
            {
                resolved.Add(tool);
            }
        }

        return resolved;
    }
}
