using System.Diagnostics.CodeAnalysis;
using Oscova.Agents.Infrastructure.Users;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Oscova.Agents.Core.Auth;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Nodes;
using Oscova.Agents.Core.ApiContracts.Nodes;
using Oscova.Agents.Infrastructure.Auth;
using Oscova.Agents.Infrastructure.Secrets;
using Oscova.Agents.Infrastructure.Plugins;
using Oscova.Agents.Secrets;
using Oscova.Agents.Core.Settings;

namespace Oscova.Agents.Host.Server.Nodes;

/// <summary>Owns connected devices and admits each operation against current central authorization.</summary>
public sealed class NodeBroker(
    IHubContext<NodeHub> hub,
    NodeCredentialStore credentials,
    IUserPermissionService permissions,
    IContentProtector protector,
    PendingDuressSelections pendingDuress,
    OperatorVaultAccess operatorVault,
    ExecutionPermissionRegistry executions,
    ExecutionEnvironmentCatalog environments,
    ILogger<NodeBroker> logger) : INodeDispatcher, IDisposable
{
    private static readonly TimeSpan AcknowledgementTimeout = TimeSpan.FromSeconds(10);
    /// <summary>Notifies browser components when this account's device availability changes.</summary>
    [SuppressMessage("Design", "MA0046", Justification = "Subscribers need only the account identity for refresh.")]
    public event Action<string>? StateChanged;

    private readonly ConcurrentDictionary<string, Connection> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingCall> _calls = new(StringComparer.Ordinal);
    private readonly Lock _providerGate = new();
    private readonly Dictionary<(Connection Connection, string ExecutionId, string Key), ExecutionPermissionRegistry.Registration> _providerExecutions = [];

    /// <summary>Returns active account identities to synchronize approved device capabilities.</summary>
    internal IReadOnlyList<NodeIdentity> ConnectedIdentities => _connections.Values
        .Where(connection => !connection.Disconnected.IsCancellationRequested).Select(connection => connection.Identity).ToArray();

    /// <summary>Registers one authenticated device connection after runtime and capability validation.</summary>
    public async Task<NodePolicy> RegisterAsync(NodeIdentity identity, string connectionId,
        NodeHello hello, Action abort, CancellationToken disconnected)
    {
        NodeProtocol.ValidateSize(hello);
        if (hello.ProtocolVersion != NodeProtocol.Version)
        {
            throw new HubException("The node connection protocol is unsupported. Use a compatible server and node release.");
        }

        if (hello.Capabilities.Select(capability => capability.Id).Distinct(StringComparer.Ordinal).Count() != hello.Capabilities.Count ||
            hello.Capabilities.Any(capability => string.IsNullOrWhiteSpace(capability.Id) || capability.Version < 1 ||
                capability.Tools.Any(tool => string.IsNullOrWhiteSpace(tool.Name)) ||
                capability.Tools.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count() != capability.Tools.Count))
        {
            throw new HubException("Capability IDs and tool names must be non-empty and unique, with positive capability versions.");
        }

        if (!await credentials.IsEnrolledAsync(identity.UserId, identity.NodeId, disconnected).ConfigureAwait(false))
        {
            throw new HubException("The node enrollment was revoked. Enroll this device again.");
        }

        var connection = new Connection(identity, connectionId, hello, abort, disconnected);
        if (!_connections.TryAdd(identity.NodeId, connection))
        {
            throw new HubException(NodeProtocol.ActiveConnectionError);
        }

        try
        {
            var policy = new NodePolicy(await permissions.GetGrantedAsync(identity.UserId, disconnected).ConfigureAwait(false));
            StateChanged?.Invoke(identity.UserId);
            return policy;
        }
        catch
        {
            _connections.TryRemove(new KeyValuePair<string, Connection>(identity.NodeId, connection));
            throw;
        }
    }

    /// <summary>Returns capability metadata only for a device owned by the requesting account.</summary>
    public NodeHello? GetConnected(string userId, string nodeId) =>
        _connections.TryGetValue(nodeId, out var connection) &&
        connection.Identity.UserId == userId && !connection.Disconnected.IsCancellationRequested
            ? connection.Hello : null;

    /// <summary>Removes only the departing connection so it cannot detach a replacement.</summary>
    public void Disconnect(string nodeId, string connectionId)
    {
        if (_connections.TryGetValue(nodeId, out var connection) && connection.Id == connectionId &&
            _connections.TryRemove(new KeyValuePair<string, Connection>(nodeId, connection)))
        {
            ReleaseProviderExecutions(connection);
            StateChanged?.Invoke(connection.Identity.UserId);
        }
    }

    /// <inheritdoc />
    public async Task<NodeReply> InvokeAsync(string userId, string nodeId, string executionId, string capability, int capabilityVersion, string tool,
        JsonElement context, JsonElement arguments, IReadOnlyList<string> requiredPermissions, CancellationToken ct, NodeCallbackHandler? callback = null)
    {
        if (!_connections.TryGetValue(nodeId, out var connection) || connection.Identity.UserId != userId)
        {
            throw new NodeUnavailableException();
        }

        if (!connection.Hello.Capabilities.Any(candidate => candidate.Id == capability && candidate.Version == capabilityVersion &&
            candidate.Tools.Any(operation => operation.Name == tool)))
        {
            throw new InvalidOperationException("The selected node does not support this operation.");
        }

        var providerToken = capability == ProjectCapability.Id && requiredPermissions.Contains(UserPermissionKeys.ProjectShell, StringComparer.Ordinal)
            ? AdmitProvider(connection, executionId, context) : CancellationToken.None;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, connection.Disconnected, providerToken);
        using var execution = executions.Register(userId, requiredPermissions, lifetime.Token);
        var invocation = new NodeInvocation(Guid.NewGuid().ToString("N"), executionId, capability, tool, context, arguments);
        try { NodeProtocol.ValidateSize(invocation); }
        catch (ArgumentException tooLarge) { return new(null, tooLarge.Message, []); }

        using var callbackLifetime = CancellationTokenSource.CreateLinkedTokenSource(execution.Token);
        var pending = new PendingCall(connection, execution, callback, callbackLifetime.Token);
        await RequireAsync(pending, execution.Token).ConfigureAwait(false);
        if (!_calls.TryAdd(invocation.Id, pending)) { throw new InvalidOperationException("The invocation ID is already active."); }

        try
        {
            var result = await hub.Clients.Client(connection.Id)
                .InvokeAsync<NodeReply>(NodeProtocol.Invoke, invocation, execution.Token).ConfigureAwait(false);
            if (Volatile.Read(ref pending.Admitted) == 0)
            {
                throw new InvalidDataException("The node returned a result without execution admission.");
            }

            NodeProtocol.ValidateSize(result);
            return result;
        }
        catch (Exception cancellationError) when (cancellationError is OperationCanceledException ||
            cancellationError is HubException && execution.Token.IsCancellationRequested)
        {
            try
            {
                await hub.Clients.Client(connection.Id).SendAsync(NodeProtocol.Cancel, invocation.Id, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not deliver cancellation for node {NodeId}, invocation {InvocationId}.", nodeId, invocation.Id);
            }

            throw new OperationCanceledException("The node operation was canceled.", cancellationError, execution.Token);
        }
        finally
        {
            _calls.TryRemove(invocation.Id, out _);
            await callbackLifetime.CancelAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Rechecks authority and dispatches a reverse request to its originating server call.</summary>
    public async Task<JsonElement> CallbackAsync(NodeIdentity identity, string connectionId, string invocationId,
        string operation, JsonElement arguments, CancellationToken ct)
    {
        NodeProtocol.ValidateSize(arguments);
        if (!_calls.TryGetValue(invocationId, out var pending) || pending.Connection.Id != connectionId ||
            pending.Connection.Identity != identity || Volatile.Read(ref pending.Admitted) == 0 || pending.Callback is null)
        {
            throw new HubException("This connection has no admitted invocation accepting callbacks.");
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, pending.CallbackToken);
        await RequireAsync(pending, lifetime.Token).ConfigureAwait(false);
        lifetime.Token.ThrowIfCancellationRequested();
        if (!_calls.TryGetValue(invocationId, out var current) || current != pending)
        {
            throw new HubException("This invocation has finished.");
        }

        var result = await pending.Callback(operation, arguments, lifetime.Token).ConfigureAwait(false);
        NodeProtocol.ValidateSize(result);
        return result;
    }

    /// <summary>Admits a server-issued call once and only on its original authenticated connection.</summary>
    public async Task AdmitAsync(NodeIdentity identity, string connectionId, string invocationId, CancellationToken ct)
    {
        if (!_calls.TryGetValue(invocationId, out var pending) ||
            pending.Connection.Id != connectionId || pending.Connection.Identity != identity)
        {
            throw new HubException("This connection has no matching pending invocation.");
        }

        pending.Execution.Token.ThrowIfCancellationRequested();
        await RequireAsync(pending, ct).ConfigureAwait(false);
        pending.Execution.Token.ThrowIfCancellationRequested();
        if (!_calls.TryGetValue(invocationId, out var current) || current != pending)
        {
            throw new HubException("This invocation is no longer pending.");
        }

        if (Interlocked.Exchange(ref pending.Admitted, 1) != 0)
        {
            throw new HubException("This invocation has already been admitted.");
        }
    }

    /// <summary>Requests cancellation of revoked work and updates every connected device for this account.</summary>
    public async Task ApplyPolicyAsync(string userId, IReadOnlyList<string> granted)
    {
        StateChanged?.Invoke(userId);
        await Task.WhenAll(_connections.Values.Where(connection => connection.Identity.UserId == userId)
            .Select(UpdateAsync)).ConfigureAwait(false);

        async Task UpdateAsync(Connection connection)
        {
            try
            {
                await AcknowledgeAsync(connection, NodeProtocol.PolicyChanged, new NodePolicy(granted),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Disconnected node {NodeId} after its permission update failed.", connection.Identity.NodeId);
            }
        }
    }

    /// <summary>Stops an owned conversation's resources on every connected device belonging to its account.</summary>
    public Task CleanupExecutionAsync(string userId, string executionId, CancellationToken ct) =>
        Task.WhenAll(_connections.Values.Where(connection => connection.Identity.UserId == userId)
            .Select(connection => CleanupProviderExecutionAsync(connection, executionId, ct)));

    /// <summary>Awaits device-owned preparation shutdown before the server applies a provider change.</summary>
    public Task StopProviderAsync(string configurationKey)
    {
        if (configurationKey != ExecutionSettings.ConfigurationKey && !environments.Environments.Any(environment => environments.ConfigurationKey(environment.Id) == configurationKey))
        { return Task.CompletedTask; }

        return Task.WhenAll(_connections.Values.Select(connection =>
            AcknowledgeAsync(connection, NodeProtocol.StopExecutionProvider, configurationKey, CancellationToken.None)));
    }

    /// <summary>Keeps remote shell resources bound to their server plugin until conversation or device cleanup.</summary>
    private CancellationToken AdmitProvider(Connection connection, string executionId, JsonElement context)
    {
        var providerId = context.Deserialize<ProjectNodeContext>()?.ProviderId
            ?? throw new InvalidOperationException("Shell execution is disabled. Select an execution environment before starting a new run.");
        Dictionary<(Connection Connection, string ExecutionId, string Key), ExecutionPermissionRegistry.Registration> added = [];
        try
        {
            lock (_providerGate)
            {
                if (!_connections.TryGetValue(connection.Identity.NodeId, out var current) || current != connection)
                { throw new NodeUnavailableException(); }

                environments.GetProvider(providerId);
                var keys = new[] { ExecutionSettings.ConfigurationKey, environments.ConfigurationKey(providerId) };
                ExecutionPermissionRegistry.Registration? authority = null;
                foreach (var key in keys.OfType<string>())
                {
                    var version = executions.GetConfigurationVersion(key);
                    var identity = (connection, executionId, key);
                    if (!_providerExecutions.TryGetValue(identity, out var registration))
                    {
                        registration = executions.RegisterConfiguration(key, version, connection.Identity.UserId,
                            connection.Disconnected, () => CleanupProviderExecutionAsync(connection, executionId, CancellationToken.None));
                        added.Add(identity, registration);
                    }

                    registration.Token.ThrowIfCancellationRequested();
                    authority ??= registration;
                }

                foreach (var entry in added) { _providerExecutions.Add(entry.Key, entry.Value); }

                return authority!.Token;
            }
        }
        catch
        {
            // Failed admission must not retain new dependencies or cancel earlier remote work.
            foreach (var registration in added.Values) { registration.Dispose(); }

            throw;
        }
    }

    private async Task CleanupProviderExecutionAsync(Connection connection, string executionId, CancellationToken ct)
    {
        try { await AcknowledgeAsync(connection, NodeProtocol.CleanupExecution, executionId, ct).ConfigureAwait(false); }
        finally { ReleaseProviderExecutions(connection, executionId); }
    }

    private void ReleaseProviderExecutions(Connection connection, string? executionId = null)
    {
        List<ExecutionPermissionRegistry.Registration> released = [];
        lock (_providerGate)
        {
            foreach (var key in _providerExecutions.Keys.Where(key => key.Connection == connection &&
                (executionId is null || key.ExecutionId == executionId)).ToArray())
            {
                _providerExecutions.Remove(key, out var registration);
                released.Add(registration!);
            }
        }
        // Cancellation can call back into this broker, so dispose registrations after releasing its lock.
        foreach (var registration in released) { registration.Dispose(); }
    }

    /// <summary>Bounds lifecycle acknowledgements so a live but unresponsive node cannot stall central administration.</summary>
    private async Task AcknowledgeAsync(Connection connection, string operation, object argument, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, connection.Disconnected);
        deadline.CancelAfter(AcknowledgementTimeout);
        try
        {
            if (!await hub.Clients.Client(connection.Id).InvokeAsync<bool>(operation, argument, deadline.Token)
                .ConfigureAwait(false))
            {
                throw new InvalidDataException("The node declined its lifecycle acknowledgement.");
            }
        }
        catch (Exception ex)
        {
            connection.Abort();
            throw new IOException($"Node '{connection.Identity.NodeId}' did not acknowledge '{operation}'. " +
                "It was disconnected. Remote cleanup is unconfirmed.", ex);
        }
    }

    /// <summary>Terminates the owned device connection after its stored credential has been revoked.</summary>
    public void Revoke(string userId, string nodeId)
    {
        if (_connections.TryGetValue(nodeId, out var connection) && connection.Identity.UserId == userId)
        {
            connection.Abort();
        }
    }

    private async Task RequireAsync(PendingCall pending, CancellationToken ct)
    {
        var connection = pending.Connection;
        var identity = connection.Identity;
        if (!_connections.TryGetValue(identity.NodeId, out var current) || current != connection ||
            connection.Disconnected.IsCancellationRequested ||
            !await credentials.IsEnrolledAsync(identity.UserId, identity.NodeId, ct).ConfigureAwait(false))
        {
            throw new UnauthorizedAccessException("The node connection is unavailable or revoked.");
        }

        if (!protector.IsAvailable(identity.UserId) || pendingDuress.Contains(identity.UserId))
        {
            throw new UnauthorizedAccessException("Unlock the account vault before using its projects.");
        }

        await operatorVault.RequireAsync(identity.UserId, ct).ConfigureAwait(false);
        foreach (var permission in pending.Execution.RequiredPermissions)
        {
            await permissions.RequireAsync(identity.UserId, permission, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        executions.Changed -= ApplyPolicyAsync;
        foreach (var connection in _connections.Values) { ReleaseProviderExecutions(connection); }
    }

    /// <summary>Binds one authenticated connection to its advertised capability catalog.</summary>
    private sealed record Connection(NodeIdentity Identity, string Id, NodeHello Hello,
        Action Abort, CancellationToken Disconnected);

    /// <summary>Retains the server's authorization record until a client result completes.</summary>
    private sealed class PendingCall(Connection connection, ExecutionPermissionRegistry.Registration execution,
        NodeCallbackHandler? callback, CancellationToken callbackToken)
    {
        public Connection Connection { get; } = connection;
        public ExecutionPermissionRegistry.Registration Execution { get; } = execution;
        public NodeCallbackHandler? Callback { get; } = callback;
        public CancellationToken CallbackToken { get; } = callbackToken;
        public int Admitted;
    }

}
