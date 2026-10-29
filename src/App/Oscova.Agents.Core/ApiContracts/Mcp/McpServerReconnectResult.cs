using Oscova.Agents.Core.Mcp;

namespace Oscova.Agents.Core.ApiContracts.Mcp;

/// <summary>
/// Returned when one or more saved MCP servers are reconnected.
/// </summary>
public sealed class McpServerReconnectResult
{
    public IReadOnlyList<McpServerConnectionResult> Connections { get; set; } = [];
}
