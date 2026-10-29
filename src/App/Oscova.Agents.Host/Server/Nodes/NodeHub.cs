using System.Text.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Oscova.Agents.Core.Nodes;
using Oscova.Agents.Core.ApiContracts.Nodes;
using Oscova.Agents.Host.Server.Security;
using Oscova.Agents.Infrastructure.Auth;

namespace Oscova.Agents.Host.Server.Nodes;

/// <summary>Accepts device-initiated connections without exposing ordinary account APIs.</summary>
[Authorize(AuthenticationSchemes = NodeAuthenticationHandler.SchemeName)]
public sealed class NodeHub(NodeBroker broker, NodePluginDeploymentService plugins) : Hub
{
    /// <summary>Starts approved package preparation after the client can receive admitted invocations.</summary>
    public void Ready() => plugins.Connected(Identity);

    /// <summary>Registers the authenticated runtime's versioned capability catalog.</summary>
    public Task<NodePolicy> Register(NodeHello hello) =>
        broker.RegisterAsync(Identity, Context.ConnectionId, hello, Context.Abort, Context.ConnectionAborted);

    /// <summary>Rechecks current authorization immediately before the node starts a server-issued call.</summary>
    public Task Admit(string invocationId) =>
        broker.AdmitAsync(Identity, Context.ConnectionId, invocationId, Context.ConnectionAborted);

    /// <summary>Accepts a reverse request only for this connection's admitted invocation.</summary>
    public Task<JsonElement> Callback(string invocationId, string operation, JsonElement arguments) =>
        broker.CallbackAsync(Identity, Context.ConnectionId, invocationId, operation, arguments, Context.ConnectionAborted);

    /// <inheritdoc />
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        broker.Disconnect(Identity.NodeId, Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    private NodeIdentity Identity => new(
        Context.User!.FindFirstValue(ClaimTypes.NameIdentifier)!,
        Context.User!.FindFirstValue(NodeCredentialStore.NodeIdClaim)!);
}
