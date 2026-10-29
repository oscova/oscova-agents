using Oscova.Agents.Core.Mcp;

namespace Oscova.Agents.Core.ApiContracts.Mcp;

/// <summary>MCP server configuration and list of currently connected tools.</summary>
public sealed class McpConfiguration
{
    public bool CanManage { get; set; }
    public bool IsOperator { get; set; }
    public IReadOnlyList<Guid> ManagedServerIds { get; set; } = [];
    public IReadOnlyList<McpServerConfig> Servers { get; set; } = [];
    public IReadOnlyList<McpTool> ConnectedTools { get; set; } = [];
}
