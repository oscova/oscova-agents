using Oscova.Agents.Core.ApiContracts.Nodes;
using System.Text.Json;

namespace Oscova.Agents.Core.Nodes;

/// <summary>Dispatches already-authorized capability operations to an account-owned connected node.</summary>
public interface INodeDispatcher
{
    /// <summary>Stops resources for one owned execution on the account's connected devices.</summary>
    Task CleanupExecutionAsync(string userId, string executionId, CancellationToken ct);

    /// <summary>Invokes one supported operation with current account grants and connection-bound admission.</summary>
    Task<NodeReply> InvokeAsync(string userId, string nodeId, string executionId, string capability, int capabilityVersion, string tool,
        JsonElement context, JsonElement arguments, IReadOnlyList<string> requiredPermissions,
        CancellationToken ct, NodeCallbackHandler? callback = null);

}
