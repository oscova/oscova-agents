using Oscova.Agents.Core.ApiContracts.Nodes;
namespace Oscova.Agents.Core.Nodes;

/// <summary>Implements one versioned set of device operations behind the shared node connection.</summary>
public interface INodeCapability : IAsyncDisposable
{
    NodeCapability Description { get; }

    /// <summary>Starts the capability's existing background lifecycle services.</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>Stops resources belonging to one server-owned execution.</summary>
    Task CleanupExecutionAsync(string executionId, CancellationToken ct);

    /// <summary>Returns the account grants required by the selected registered operation.</summary>
    IReadOnlyList<string> GetRequiredPermissions(string operation);

    /// <summary>Executes an admitted operation under its supplied cancellation token.</summary>
    Task<NodeReply> InvokeAsync(NodeInvocation invocation, CancellationToken ct, NodeCallbackHandler? callback = null);

    /// <summary>Stops managed processes and releases connection-owned execution state.</summary>
    Task ResetAsync();
}
