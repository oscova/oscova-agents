using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR;
using Oscova.Agents.Core.Auth;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Core.Nodes;
using Oscova.Agents.Core.ApiContracts.Nodes;
using Oscova.Agents.Harness;
using Oscova.Agents.Infrastructure.Plugins;

namespace Oscova.Agents.Host.Server.Nodes;

/// <summary>Connects a device's capabilities to the server without starting a second agent runtime.</summary>
public sealed class NodeClient
{
    private readonly HubConnection _connection;
    private readonly string _userId;
    private readonly IReadOnlyDictionary<string, INodeCapability> _capabilities;
    private readonly ExecutionPermissionRegistry _executions;
    private readonly ExecutionEnvironmentCatalog _environments;
    private readonly NodeHello _hello;
    private readonly Func<string, NodePolicy?, Task> _status;
    private readonly ILogger<NodeClient> _logger;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Dictionary<string, Call> _calls = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource _session = new();
    private bool _ready;

    /// <summary>Connects the supplied capability instances to one enrolled account and server.</summary>
    public NodeClient(HubConnection connection, string userId, IReadOnlyList<INodeCapability> capabilities,
        ExecutionPermissionRegistry executions, ExecutionEnvironmentCatalog environments, NodeHello hello, Func<string, NodePolicy?, Task> status,
        ILogger<NodeClient> logger)
    {
        _connection = connection;
        _userId = userId;
        _capabilities = capabilities.ToDictionary(capability => capability.Description.Id, StringComparer.Ordinal);
        _executions = executions;
        _environments = environments;
        _hello = hello;
        _status = status;
        _logger = logger;
    }

    /// <summary>Runs until canceled or the transport reports a terminal connection failure.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var invoke = _connection.On<NodeInvocation, NodeReply>(NodeProtocol.Invoke, InvokeAsync);
        using var cancel = _connection.On<string>(NodeProtocol.Cancel, CancelAsync);
        using var cleanup = _connection.On<string, bool>(NodeProtocol.CleanupExecution, CleanupExecutionAsync);
        using var stopProvider = _connection.On<string, bool>(NodeProtocol.StopExecutionProvider, async key =>
        { await _environments.StopAsync(key == ExecutionSettings.ConfigurationKey ? null : key).ConfigureAwait(false); return true; });
        using var policy = _connection.On<NodePolicy, bool>(NodeProtocol.PolicyChanged, ApplyPolicyAsync);
        _connection.Reconnecting += OnReconnectingAsync;
        Task ReconnectedAsync(string? connectionId) => OnReconnectedAsync(connectionId, ct);
        _connection.Reconnected += ReconnectedAsync;
        _connection.Closed += OnClosedAsync;
        try
        {
            await Task.WhenAll(_capabilities.Values.Select(capability => capability.StartAsync(ct))).ConfigureAwait(false);
            await _status("Connecting", null).ConfigureAwait(false);
            await _connection.StartAsync(ct).ConfigureAwait(false);
            await RegisterAsync(ct).ConfigureAwait(false);
            await _closed.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            _connection.Reconnecting -= OnReconnectingAsync;
            _connection.Reconnected -= ReconnectedAsync;
            _connection.Closed -= OnClosedAsync;
            invoke.Dispose();
            cancel.Dispose();
            cleanup.Dispose();
            stopProvider.Dispose();
            policy.Dispose();
            try { await ResetAsync().ConfigureAwait(false); }
            finally
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
                await Task.WhenAll(_capabilities.Values.Select(capability => capability.DisposeAsync().AsTask()))
                    .ConfigureAwait(false);
                _session.Dispose();
                _lifecycle.Dispose();
                await _status("Stopped", null).ConfigureAwait(false);
            }
        }
    }

    private async Task RegisterAsync(CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var policy = await _connection.InvokeAsync<NodePolicy>(NodeProtocol.Register, _hello, ct).ConfigureAwait(false);
            lock (_gate) { _ready = true; }

            await _connection.SendAsync(NodeProtocol.Ready, ct).ConfigureAwait(false);
            await _status("Connected", policy).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task<NodeReply> InvokeAsync(NodeInvocation invocation)
    {
        Call? call = null;
        try
        {
            NodeProtocol.ValidateSize(invocation);
            INodeCapability capability;
            lock (_gate)
            {
                if (!_ready) { throw new InvalidOperationException("The node is not ready. Wait for Connected."); }

                capability = _capabilities.GetValueOrDefault(invocation.Capability)
                    ?? throw new InvalidOperationException("The requested capability is unavailable on this node.");
                if (_calls.ContainsKey(invocation.Id)) { throw new InvalidOperationException("The invocation is already active."); }

                call = new(invocation.ExecutionId, _executions.Register(_userId, capability.GetRequiredPermissions(invocation.Tool), _session.Token));
                _calls.Add(invocation.Id, call);
            }
            // Admission rechecks central permissions after this call is registered for cancellation.
            await _connection.InvokeAsync(NodeProtocol.Admit, invocation.Id, call.Permission.Token).ConfigureAwait(false);
            call.Permission.Token.ThrowIfCancellationRequested();
            return await capability.InvokeAsync(invocation, call.Permission.Token, SendCallbackAsync).ConfigureAwait(false);

            async Task<JsonElement> SendCallbackAsync(string operation, JsonElement arguments, CancellationToken callbackToken)
            {
                NodeProtocol.ValidateSize(arguments);
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(callbackToken, call.Permission.Token);
                var result = await _connection.InvokeAsync<JsonElement>(NodeProtocol.Callback,
                    invocation.Id, operation, arguments, lifetime.Token).ConfigureAwait(false);
                NodeProtocol.ValidateSize(result);
                return result;
            }

        }
        catch (OperationCanceledException)
        {
            return new(null, "The node operation was canceled. Completed changes were not rolled back.", []);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Node operation {InvocationId} failed for {Capability} tool {Tool}.",
                invocation.Id, invocation.Capability, invocation.Tool);
            // Tool failures already contain safe recovery text for the agent.
            var message = ex switch
            {
                ToolFailureException failure => failure.Fix is null ? failure.Message : $"{failure.Message} {failure.Fix}",
                ArgumentException or InvalidOperationException or UnauthorizedAccessException => ex.Message,
                _ => "The node operation failed. Check the node terminal for details."
            };
            return new(null, message, []);
        }
        finally
        {
            if (call is not null)
            {
                lock (_gate) { _calls.Remove(invocation.Id); }

                call.Permission.Dispose();
                call.Completion.TrySetResult();
            }
        }
    }

    private Task CancelAsync(string id)
    {
        lock (_gate)
        {
            return _calls.TryGetValue(id, out var call) ? call.Permission.CancelAsync() : Task.CompletedTask;
        }
    }

    private async Task<bool> CleanupExecutionAsync(string executionId)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            Call[] calls;
            lock (_gate) { calls = _calls.Values.Where(call => call.ExecutionId == executionId).ToArray(); }

            await Task.WhenAll(calls.Select(call => call.Permission.CancelAsync())).ConfigureAwait(false);
            await Task.WhenAll(calls.Select(call => call.Completion.Task)).ConfigureAwait(false);
            await Task.WhenAll(_capabilities.Values.Select(capability =>
                capability.CleanupExecutionAsync(executionId, CancellationToken.None))).ConfigureAwait(false);
            return true;
        }
        finally { _lifecycle.Release(); }
    }

    private async Task<bool> ApplyPolicyAsync(NodePolicy policy)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            Call[] affected;
            lock (_gate)
            {
                affected = _calls.Values.Where(call => call.Permission.RequiredPermissions.Any(permission =>
                    !policy.GrantedPermissions.Contains(permission, StringComparer.Ordinal))).ToArray();
            }

            await _executions.ApplyAsync(_userId, policy.GrantedPermissions).ConfigureAwait(false);
            // Acknowledge revocation only after the affected capabilities finish cleanup.
            await Task.WhenAll(affected.Select(call => call.Completion.Task)).ConfigureAwait(false);
            await _status("Connected", policy).ConfigureAwait(false);
            return true;
        }
        finally { _lifecycle.Release(); }
    }

    private async Task ResetAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            Call[] calls;
            lock (_gate)
            {
                _ready = false;
                calls = _calls.Values.ToArray();
            }

            await _session.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(calls.Select(call => call.Completion.Task)).ConfigureAwait(false);
            await Task.WhenAll(_capabilities.Values.Select(capability => capability.ResetAsync())).ConfigureAwait(false);
            await _environments.StopAsync().ConfigureAwait(false);
            _session.Dispose();
            _session = new();
        }
        finally { _lifecycle.Release(); }
    }

    private async Task OnReconnectingAsync(Exception? error)
    {
        await _status("Disconnected, reconnecting", null).ConfigureAwait(false);
        await ResetAsync().ConfigureAwait(false);
    }

    private async Task OnReconnectedAsync(string? connectionId, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                try { await RegisterAsync(ct).ConfigureAwait(false); return; }
                // A lost disconnect can leave the previous transport registered briefly after reconnect.
                catch (HubException ex) when (ex.Message.EndsWith(NodeProtocol.ActiveConnectionError, StringComparison.Ordinal))
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { _closed.TrySetException(ex); }
    }

    private Task OnClosedAsync(Exception? error)
    {
        _closed.TrySetException(error ?? new IOException("The node connection closed. Start the node again to reconnect."));
        return Task.CompletedTask;
    }

    /// <summary>Retains cancellation and completion while a transport invocation is executing.</summary>
    private sealed class Call(string executionId, ExecutionPermissionRegistry.Registration permission)
    {
        public string ExecutionId { get; } = executionId;
        public ExecutionPermissionRegistry.Registration Permission { get; } = permission;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
