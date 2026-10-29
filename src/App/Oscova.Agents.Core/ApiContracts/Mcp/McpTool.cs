namespace Oscova.Agents.Core.ApiContracts.Mcp;

/// <summary>A single connected MCP tool with its qualified and original names.</summary>
public sealed class McpTool
{
    public string QualifiedName { get; set; } = string.Empty;
    public string ServerName { get; set; } = string.Empty;
    public string OriginalName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}
