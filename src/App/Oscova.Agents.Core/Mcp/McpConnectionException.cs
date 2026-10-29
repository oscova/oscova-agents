namespace Oscova.Agents.Core.Mcp;

/// <summary>Supplies safe recovery text when an agent requires an unavailable MCP connection.</summary>
public sealed class McpConnectionException(string serverId, string? error) : InvalidOperationException(
    $"MCP connection '{serverId}' could not start. " + (error == MissingDockerMessage
        ? MissingDockerMessage
        : "Open MCP server settings, check its configuration on the execution device, and reconnect."))
{
    public string ServerId { get; } = serverId;
    public bool DockerMissing { get; } = error == MissingDockerMessage;
    public const string MissingDockerMessage = "Docker was not found on the execution device. Install Docker or select native MCP execution.";
}
