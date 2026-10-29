using Oscova.Agents.Core.Mcp.Registry;

namespace Oscova.Agents.Core.Interfaces;

/// <summary>
/// Searches the official MCP Registry for installable servers.
/// </summary>
public interface IMcpRegistryService
{
    /// <summary>
    /// Returns latest active servers matching the query.
    /// </summary>
    Task<IReadOnlyList<McpRegistryServer>> SearchAsync(string query, CancellationToken cancellationToken);
}

/// <summary>
/// Raised when the MCP Registry cannot complete a search request.
/// </summary>
public sealed class McpRegistryException : Exception
{
    public McpRegistryException(string message) : base(message) { }
    public McpRegistryException(string message, Exception innerException) : base(message, innerException) { }
}
