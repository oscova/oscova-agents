namespace Oscova.Agents.Core.Mcp;

/// <summary>
/// Reports the outcome of connecting one saved MCP server to the live tool hub.
/// </summary>
public sealed class McpServerConnectionResult
{
    public Guid ServerId { get; set; }

    public string ServerName { get; set; } = string.Empty;

    public bool IsConnected { get; set; }
    public bool DeviceOffline { get; set; }

    public int ToolCount { get; set; }

    public string? Error { get; set; }
}
