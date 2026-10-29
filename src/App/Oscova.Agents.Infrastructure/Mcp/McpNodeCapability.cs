using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Oscova.Agents.Core.ApiContracts.Nodes;
using Oscova.Agents.Core.Auth;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Mcp;
using Oscova.Agents.Core.Nodes;
using Oscova.Agents.Core.Projects;
using Oscova.Agents.Execution;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Infrastructure.Mcp;

/// <summary>Hosts MCP transports on an owned device while discovery and tool policy stay central.</summary>
public sealed class McpNodeCapability(string userId, IReadOnlyList<string> privateRoots,
    IExecutionEnvironmentCatalog executionEnvironments, IHttpClientFactory httpClients, ILoggerFactory logs) : INodeCapability
{
    private readonly ConcurrentDictionary<string, LocalTransport> _sessions = new(StringComparer.Ordinal);
    private readonly RunContextAccessor _runs = new();

    public NodeCapability Description => McpNodeCapabilityContract.Description;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public IReadOnlyList<string> GetRequiredPermissions(string operation) => operation switch
    {
        McpNodeCapabilityContract.Open or McpNodeCapabilityContract.Send => [UserPermissionKeys.Projects],
        McpNodeCapabilityContract.Close => [],
        _ => throw new ArgumentException("The MCP transport operation is unsupported.")
    };

    /// <inheritdoc />
    public async Task<NodeReply> InvokeAsync(NodeInvocation invocation, CancellationToken ct, NodeCallbackHandler? callback = null)
    {
        _ = GetRequiredPermissions(invocation.Tool);
        var context = invocation.Context.Deserialize<McpNodeContext>()
            ?? throw new ArgumentException("MCP session context is required.");
        if (!Guid.TryParseExact(context.SessionId, "N", out _))
        {
            throw new ArgumentException("An MCP session identity is required.");
        }

        await using var scope = _runs.SetContext(new RunContext
        {
            UserId = userId, ExecutionKind = RunExecutionKind.WebChat,
            ExecutionId = context.SessionId, ProjectId = context.ProjectId, AirGapped = context.AirGapped
        });
        if (invocation.Tool == McpNodeCapabilityContract.Open)
        {
            if (callback is null) { throw new ArgumentException("The MCP receive callback is required."); }

            var root = context.ProjectId is null ? null : ProjectRootValidator.ValidateNode(context.RootPath!, privateRoots);
            var server = invocation.Arguments.Deserialize<McpServerConfig>()
                ?? throw new ArgumentException("MCP launch configuration is required.");
            if (!server.IsValidForConnect()) { throw new ArgumentException("MCP launch configuration is incomplete."); }

            if (server.Transport == McpTransportType.Stdio && string.IsNullOrWhiteSpace(server.WorkingDirectory))
            {
                server.WorkingDirectory = root?.Path;
            }
            // Setup checks launch the program without borrowing or mounting an unrelated project.
            IReadOnlyList<AllowedDirectory> directories = root is null ? [] :
                [new(root.Path, "project", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite,
                    AgentPath: "/project", IsWorkingDirectory: true)];
            var factory = await McpTransportFactory.CreateAsync(server, directories, executionEnvironments, httpClients,
                logs.CreateLogger<McpNodeCapability>(), TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            var transport = await factory.ConnectAsync(ct).ConfigureAwait(false);
            var session = new LocalTransport(context, transport);
            if (!_sessions.TryAdd(context.SessionId, session))
            {
                await session.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException("The MCP session already exists.");
            }

            try
            {
                await callback(McpNodeCapabilityContract.Ready,
                    JsonSerializer.SerializeToElement(transport.SessionId), ct).ConfigureAwait(false);
                await foreach (var message in transport.MessageReader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    await callback(McpNodeCapabilityContract.Receive,
                        JsonSerializer.SerializeToElement(message, McpJsonUtilities.DefaultOptions), ct).ConfigureAwait(false);
                }
            }
            finally
            {
                await session.DisposeAsync().ConfigureAwait(false);
                _sessions.TryRemove(new KeyValuePair<string, LocalTransport>(context.SessionId, session));
            }
        }
        else if (_sessions.TryGetValue(context.SessionId, out var session))
        {
            if (session.Context != context) { throw new UnauthorizedAccessException("The MCP transport belongs to a different project context."); }

            if (invocation.Tool == McpNodeCapabilityContract.Close)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                var message = invocation.Arguments.Deserialize<JsonRpcMessage>(McpJsonUtilities.DefaultOptions)
                    ?? throw new ArgumentException("An MCP protocol message is required.");
                await session.Transport.SendMessageAsync(message, ct).ConfigureAwait(false);
            }
        }
        else if (invocation.Tool != McpNodeCapabilityContract.Close)
        {
            throw new InvalidOperationException("The MCP transport is unavailable. Start a new request to reconnect.");
        }

        return new(JsonSerializer.SerializeToElement(new { }), null, []);
    }

    /// <inheritdoc />
    public async Task CleanupExecutionAsync(string executionId, CancellationToken ct)
    {
        if (_sessions.TryGetValue(executionId, out var session)) { await session.DisposeAsync().ConfigureAwait(false); }
    }

    /// <inheritdoc />
    public Task ResetAsync() => Task.WhenAll(_sessions.Values.Select(session => session.DisposeAsync().AsTask()));

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await ResetAsync().ConfigureAwait(false);

    /// <summary>Joins explicit closure and receive-loop cleanup onto the same transport disposal.</summary>
    private sealed class LocalTransport(McpNodeContext context, ITransport transport) : IAsyncDisposable
    {
        private readonly Lock _gate = new();
        private Task? _dispose;
        public McpNodeContext Context { get; } = context;
        public ITransport Transport { get; } = transport;
        public ValueTask DisposeAsync()
        {
            lock (_gate) { return new(_dispose ??= Transport.DisposeAsync().AsTask()); }
        }
    }
}
