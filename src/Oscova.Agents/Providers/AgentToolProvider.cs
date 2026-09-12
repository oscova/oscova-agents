using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Oscova.Agents.Collections;
using Oscova.Agents.Policies;

namespace Oscova.Agents.Providers;

/// <summary>
/// Provides a filtered subset of crew tools as <see cref="AIContext.Tools"/> for a
/// <see cref="ChatClientAgent"/> and applies sparse agent-specific governance overrides.
/// Only tools whose qualified names appear in <paramref name="allowedToolNames"/> are exposed.
/// <see cref="ToolApprovalMode.RequireApproval"/> wraps the tool with approval semantics;
/// <see cref="ToolApprovalMode.Deny"/> omits the tool entirely. When the agent is used as a
/// tool (sub-agent), approval-required tools are omitted rather than wrapped because no approval
/// channel exists to route the request. Reflected confidentiality markers are also enforced.
/// </summary>
public class AgentToolProvider(
    IEnumerable<string> allowedToolNames,
    CrewToolCollection tools,
    IEnumerable<AgentToolPolicy>? toolPolicies = null,
    bool airGapped = false,
    bool suppressApprovalRequiredTools = false,
    bool isConfidential = false) : AIContextProvider
{
    private readonly AgentToolResolver _resolver = new(
        tools,
        toolPolicies,
        airGapped,
        suppressApprovalRequiredTools,
        isConfidential);

    /// <inheritdoc />
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var agentTools = _resolver.ResolveAll(allowedToolNames);
        return new ValueTask<AIContext>(new AIContext { Tools = agentTools });
    }
}
