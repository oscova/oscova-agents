using Oscova.Agents.Core.ApiContracts.Audit;
using Oscova.Agents.Core.ApiContracts.Dashboard;
using Oscova.Agents.Policies;

namespace Oscova.Agents.Core.Conversation;

/// <summary>
/// Builds durable egress audit lines from one completed chat or task run.
/// </summary>
public static class EgressAuditEventBuilder
{
    /// <summary>
    /// Builds provider and tool egress lines for one finished run, keeping only real egress.
    /// </summary>
    public static IReadOnlyList<EgressAuditRecord> BuildRunLines(
        RunContext context,
        IReadOnlyList<ProviderEgressDescriptor> egress,
        IReadOnlyList<ToolInvocationRecord> tools,
        string source,
        string? threadId,
        string? runId,
        string? taskId,
        DateTimeOffset timestamp)
    {
        var lines = new List<EgressAuditRecord>();

        foreach (var descriptor in egress)
        {
            if (descriptor.Egress != EgressClass.Cloud)
            {
                continue;
            }

            lines.Add(new EgressAuditRecord
            {
                Timestamp = timestamp,
                Kind = EgressAuditKinds.Provider,
                Source = source,
                ThreadId = threadId,
                RunId = runId,
                TaskId = taskId,
                AgentId = descriptor.AgentId,
                ProviderId = descriptor.ProviderId,
                ModelId = descriptor.ModelId
            });
        }

        foreach (var tool in tools)
        {
            if (tool.NetworkScope != NetworkScope.Egress)
            {
                continue;
            }

            // Pre-dispatch refusals count as failures, but never as network sends.
            var invocationCount = tool.Succeeded + tool.Failed - tool.NotDispatched;
            for (var invocationIndex = 0; invocationIndex < invocationCount; invocationIndex++)
            {
                lines.Add(new EgressAuditRecord
                {
                    Timestamp = timestamp,
                    Kind = EgressAuditKinds.Tool,
                    Source = source,
                    ThreadId = threadId,
                    RunId = runId,
                    TaskId = taskId,
                    AgentId = context.CurrentAgentId,
                    ToolName = string.IsNullOrEmpty(tool.ToolName) ? null : tool.ToolName,
                    McpServerId = tool.McpServerId
                });
            }
        }

        return lines;
    }
}
