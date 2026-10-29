namespace Oscova.Agents.Core.ApiContracts.Agents;

/// <summary>A group of tools belonging to one MCP server, used in the agent editor.</summary>
public sealed class ToolGroup
{
    public string ServerName { get; set; } = string.Empty;
    public IReadOnlyList<ToolDefinition> Tools { get; set; } = [];
}
