using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Oscova.Agents.Core.ApiContracts.Nodes;
using Oscova.Agents.Core.Auth;
using Oscova.Agents.Core.Mcp;
using Oscova.Agents.Core.Nodes;
using Oscova.Agents.Core.Settings;

namespace Oscova.Agents.Infrastructure.Mcp;

/// <summary>Runs the existing MCP client over an owned node's duplex transport.</summary>
internal sealed class NodeMcpClientTransport(INodeDispatcher nodes, string userId,
    ProjectDefinition? project, McpServerConfig server, bool airGapped, ILoggerFactory logs) : IClientTransport
{
    public string Name => server.Name;

    /// <inheritdoc />
    public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var transport = new Session(nodes, userId, project, server, airGapped, logs);
        try
        {
            await transport.OpenAsync(cancellationToken).ConfigureAwait(false);
            return transport;
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Uses one admitted open request for incoming frames and explicit acknowledgement for closure.</summary>
    private sealed class Session : TransportBase
    {
        private readonly INodeDispatcher _nodes;
        private readonly string _userId;
        private readonly string _nodeId;
        private readonly McpServerConfig _server;
        private readonly McpNodeContext _context;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _gate = new();
        private Task? _pump;
        private Task? _dispose;

        public Session(INodeDispatcher nodes, string userId, ProjectDefinition? project,
            McpServerConfig server, bool airGapped, ILoggerFactory logs) : base(server.Name, logs)
        {
            _nodes = nodes;
            _userId = userId;
            _nodeId = server.NodeId ?? throw new ArgumentException("An assigned device is required.");
            _server = server;
            _context = new(Guid.NewGuid().ToString("N"), project?.Id, project?.RootPath, airGapped);
        }

        public async Task OpenAsync(CancellationToken ct)
        {
            _pump = PumpAsync();
            await _ready.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        private async Task PumpAsync()
        {
            try
            {
                var reply = await InvokeAsync(McpNodeCapabilityContract.Open,
                    JsonSerializer.SerializeToElement(_server), _lifetime.Token, OnMessageAsync).ConfigureAwait(false);
                RequireSuccess(reply);
                var ended = new IOException("The node MCP transport closed.");
                _ready.TrySetException(ended);
                SetDisconnected();
            }
            catch (Exception ex)
            {
                _ready.TrySetException(ex);
                SetDisconnected(_lifetime.IsCancellationRequested ? null : ex);
            }
        }

        private async Task<JsonElement> OnMessageAsync(string operation, JsonElement value, CancellationToken ct)
        {
            if (operation == McpNodeCapabilityContract.Ready)
            {
                if (_ready.Task.IsCompleted) { throw new InvalidDataException("The MCP transport was already opened."); }

                SessionId = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                SetConnected();
                _ready.TrySetResult();
            }
            else if (operation == McpNodeCapabilityContract.Receive && _ready.Task.IsCompletedSuccessfully)
            {
                var message = value.Deserialize<JsonRpcMessage>(McpJsonUtilities.DefaultOptions)
                    ?? throw new InvalidDataException("An MCP protocol message is required.");
                await WriteMessageAsync(message, ct).ConfigureAwait(false);
            }
            else { throw new InvalidDataException("The node returned an unexpected MCP transport operation."); }

            return JsonSerializer.SerializeToElement<object?>(null);
        }

        /// <inheritdoc />
        public override async Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
        {
            if (!IsConnected) { throw new IOException("The node MCP transport is disconnected."); }

            RequireSuccess(await InvokeAsync(McpNodeCapabilityContract.Send,
                JsonSerializer.SerializeToElement(message, McpJsonUtilities.DefaultOptions), cancellationToken)
                .ConfigureAwait(false));
        }

        private Task<NodeReply> InvokeAsync(string operation, JsonElement arguments,
            CancellationToken ct, NodeCallbackHandler? callback = null) =>
            _nodes.InvokeAsync(_userId, _nodeId, _context.SessionId,
                McpNodeCapabilityContract.Id, McpNodeCapabilityContract.Version, operation,
                JsonSerializer.SerializeToElement(_context), arguments,
                operation == McpNodeCapabilityContract.Close ? [] : [UserPermissionKeys.Projects], ct, callback);

        private static void RequireSuccess(NodeReply reply)
        {
            if (reply.Failure is { } failure) { throw new IOException(failure); }
        }

        /// <inheritdoc />
        public override ValueTask DisposeAsync()
        {
            lock (_gate) { return new(_dispose ??= CloseAsync()); }
        }

        private async Task CloseAsync()
        {
            try
            {
                if (IsConnected)
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    RequireSuccess(await InvokeAsync(McpNodeCapabilityContract.Close,
                        JsonSerializer.SerializeToElement(new { }), deadline.Token).ConfigureAwait(false));
                }
            }
            finally
            {
                await _lifetime.CancelAsync().ConfigureAwait(false);
                if (_pump is not null) { await _pump.ConfigureAwait(false); }

                SetDisconnected();
                _lifetime.Dispose();
            }
        }
    }
}
