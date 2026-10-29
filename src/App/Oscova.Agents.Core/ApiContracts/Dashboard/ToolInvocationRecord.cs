using Oscova.Agents.Policies;

namespace Oscova.Agents.Core.ApiContracts.Dashboard;

/// <summary>
/// Per-tool invocation outcome counts within one run.
/// <c>ToolName</c> is the plain name, empty for an agent called as a tool, which sets <c>AgentId</c>;
/// <c>McpServerId</c> keeps equal plain names from two MCP servers apart.
/// </summary>
public sealed record ToolInvocationRecord(
    string ToolName,
    int Succeeded,
    int Failed,
    NetworkScope NetworkScope = NetworkScope.None,
    int NotDispatched = 0,
    string? McpServerId = null,
    string? AgentId = null);
