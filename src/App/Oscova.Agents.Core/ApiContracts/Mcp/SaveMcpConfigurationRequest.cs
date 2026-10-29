using Oscova.Agents.Core.Mcp;

namespace Oscova.Agents.Core.ApiContracts.Mcp;

/// <summary>Carries the list of MCP server configs to persist.</summary>
public sealed class SaveMcpConfigurationRequest
{
    public bool ConfirmInterruption { get; set; }
    public IReadOnlyList<McpServerConfig> Servers { get; set; } = [];
}
