using Microsoft.Extensions.AI;
using Oscova.Agents.Runtime;

namespace Oscova.Agents.Core.Mcp
{
    public interface IMcpServerHub : IAsyncDisposable
    {
        IReadOnlyList<AIFunction> Tools { get; }

        /// <summary>
        /// Per-tool metadata keyed by qualified tool name.
        /// Provides the original tool name, server name, and declared network scope.
        /// </summary>
        IReadOnlyDictionary<string, ExternalToolMetadata> ToolMetadata { get; }

        /// <summary>
        /// Refreshes consumers after an unexpected terminal connection change.
        /// </summary>
        Action? ToolCollectionChanged { get; set; }

        Task LoadToolsAsync(IEnumerable<McpServerConfig> servers, CancellationToken cancellationToken);

        Task<McpServerConnectionResult> AddServerAsync(McpServerConfig server, CancellationToken cancellationToken);

        Task RemoveServerAsync(Guid serverId, CancellationToken cancellationToken);

        Task<McpServerConnectionResult> RestartServerAsync(McpServerConfig server, CancellationToken cancellationToken);
    }
}
