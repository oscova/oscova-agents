using Oscova.Agents.Core.Mcp;

namespace Oscova.Agents.Core.ApiContracts.Mcp;

/// <summary>
/// Returned when a server is persisted and an immediate MCP connection attempt is made.
/// </summary>
public sealed class McpServerSaveResult
{
    public McpServerConfig Server { get; set; } = new();

    public McpServerConnectionResult Connection { get; set; } = new();
}
