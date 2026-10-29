using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Oscova.Agents.Core.ApiContracts.Nodes;
using Oscova.Agents.Core.Auth;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Nodes;
using Oscova.Agents.Core.Notifications;
using Oscova.Agents.Core.Projects;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Infrastructure.Auth;
using Oscova.Agents.Infrastructure.Plugins.Registry;
using Oscova.Agents.Notifications;
using Oscova.Agents.Plugins;
using Oscova.Agents.Harness;
using Oscova.Agents.Core.Localization;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Infrastructure.Skills;
using Oscova.Agents.Secrets;

namespace Oscova.Agents.Host.Server.Nodes;

/// <summary>Prepares approved packages after node connection and exposes their readiness without device configuration.</summary>
public sealed class NodePluginDeploymentService(NodeBroker nodes, NodeCredentialStore credentials,
    IAgentPluginCatalog plugins, PluginActivationSettings activation, IUserPermissionService permissions,
    ExecutionPermissionRegistry executions, NodeProjectExecutor results,
    NotificationHub<NotificationMessage> notifications,
    NotificationHub<NodePluginStatus> updates, IServerTextLocalizer text, UserServiceManager users,
    ILogger<NodePluginDeploymentService> logger, IContentProtector protector, NetworkSettings network,
    ExecutionSettingsService executionSettings, TimeProvider clock)
    : BackgroundService, INodePluginExecutor
{
    private readonly Channel<NodeIdentity> _pending = Channel.CreateUnbounded<NodeIdentity>();
    private readonly ConcurrentDictionary<(string User, string Node, string Plugin), Deployment> _states = new();

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        executions.Changed += PermissionsChangedAsync;
        executionSettings.Changed += ExecutionChanged;
        nodes.StateChanged += NodeStateChanged;
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        executions.Changed -= PermissionsChangedAsync;
        executionSettings.Changed -= ExecutionChanged;
        nodes.StateChanged -= NodeStateChanged;
        return base.StopAsync(cancellationToken);
    }

    private Task PermissionsChangedAsync(string userId, IReadOnlyList<string> permissions)
    {
        foreach (var identity in nodes.ConnectedIdentities.Where(identity => identity.UserId == userId)) { Connected(identity); }

        return Task.CompletedTask;
    }

    private async void NodeStateChanged(string userId)
    {
        try
        {
            foreach (var pluginId in _states.Keys.Where(key => key.User == userId).Select(key => key.Plugin).Distinct())
            {
                await PublishStatusAsync(userId, pluginId, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "Plugin status could not refresh for account {UserId}.", userId); }
    }

    /// <summary>Reconciles connected devices after activation changes or an account's vault unlock.</summary>
    public void ActivationChanged(string? userId = null)
    {
        foreach (var identity in nodes.ConnectedIdentities.Where(identity => userId is null || identity.UserId == userId))
        {
            _pending.Writer.TryWrite(identity);
            NodeStateChanged(identity.UserId);
        }
    }

    /// <summary>Rechecks execution readiness on connected devices after the operator changes execution policy.</summary>
    private void ExecutionChanged(object? sender, EventArgs args)
    {
        foreach (var identity in nodes.ConnectedIdentities) { Connected(identity); }
    }

    /// <summary>Queues package preparation after the node has completed its connection handshake.</summary>
    public void Connected(NodeIdentity identity)
    {
        foreach (var key in _states.Keys.Where(key => key.User == identity.UserId && key.Node == identity.NodeId))
        {
            if (_states.TryGetValue(key, out var state)) { _states[key] = state with { State = "preparing", Tools = [] }; }
        }

        _pending.Writer.TryWrite(identity);
    }

    /// <summary>Returns aggregate readiness per plugin without exposing other accounts' device lists.</summary>
    public IReadOnlyList<NodePluginStatus> GetStatus(string userId, bool isOperator) => _states
        .Where(item => (isOperator || item.Key.User == userId) && activation.IsEnabled(item.Key.Plugin) &&
            nodes.GetConnected(item.Key.User, item.Key.Node) is not null)
        .GroupBy(item => item.Key.Plugin, StringComparer.Ordinal)
        .Select(group => new NodePluginStatus(group.Key,
            group.Where(item => item.Value.State == "ready").Select(item => item.Value.DeviceName).ToArray(),
            group.Where(item => item.Value.State == "preparing").Select(item => item.Value.DeviceName).ToArray(),
            group.Where(item => item.Value.State == "unavailable").Select(item => item.Value.DeviceName).ToArray()))
        .ToArray();

    /// <summary>Retries only currently unavailable deployments of the requested enabled plugin.</summary>
    public void Retry(string pluginId)
    {
        foreach (var item in _states.Where(item => item.Key.Plugin == pluginId && item.Value.State == "unavailable"))
        {
            if (nodes.GetConnected(item.Key.User, item.Key.Node) is not null)
            {
                _pending.Writer.TryWrite(new(item.Key.User, item.Key.Node));
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var identity in _pending.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try { await PrepareAsync(identity, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Plugin preparation could not complete for node {NodeId}.", identity.NodeId); }
        }
    }

    private async Task PrepareAsync(NodeIdentity identity, CancellationToken ct)
    {
        // Node connections can arrive before sign-in makes their encrypted display names available.
        if (!protector.IsAvailable(identity.UserId)) { return; }

        if (nodes.GetConnected(identity.UserId, identity.NodeId)?.Capabilities.Any(item =>
            item.Id == PluginCapability.Id && item.Version == PluginCapability.Version) != true) { return; }

        var granted = await permissions.GetGrantedAsync(identity.UserId, ct).ConfigureAwait(false);
        if (!granted.Contains(UserPermissionKeys.Projects, StringComparer.Ordinal))
        {
            foreach (var key in _states.Keys.Where(key => key.User == identity.UserId && key.Node == identity.NodeId))
            {
                _states.TryRemove(key, out _);
                await PublishStatusAsync(identity.UserId, key.Plugin, ct).ConfigureAwait(false);
            }

            return;
        }

        var device = (await credentials.ListAsync(identity.UserId, ct).ConfigureAwait(false))
            .FirstOrDefault(node => node.Id == identity.NodeId);
        if (device is null) { return; }

        foreach (var plugin in plugins.Plugins.Where(plugin => activation.IsEnabled(plugin.Manifest.Id) &&
            (plugin.Toolsets.Any(toolset => toolset.SupportsNodeExecution) ||
             plugin.ExecutionProviders.Any(provider => provider.Definition.SupportsNodeExecution) || plugin.ContainerLaunchers.Count > 0)))
        {
            var key = (identity.UserId, identity.NodeId, plugin.Manifest.Id);
            var previous = _states.GetValueOrDefault(key);
            if (previous?.State == "ready") { continue; }

            _states[key] = new(null, "preparing", [], previous?.Failure, device.Name);
            await PublishStatusAsync(identity.UserId, plugin.Manifest.Id, ct).ConfigureAwait(false);
            // An unresponsive device must release the shared package queue after a bounded installation window.
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10), clock);
            try
            {
                var package = ReadPackage(plugin);
                var configKey = PluginCapability.ConfigurationKey(package.PluginId);
                using var execution = executions.RegisterConfiguration(configKey, executions.GetConfigurationVersion(configKey), identity.UserId, ct);
                using var policy = executions.RegisterConfiguration(ExecutionSettings.ConfigurationKey,
                    executions.GetConfigurationVersion(ExecutionSettings.ConfigurationKey), identity.UserId, execution.Token);
                if (!activation.IsEnabled(package.PluginId)) { continue; }

                using var preparation = CancellationTokenSource.CreateLinkedTokenSource(policy.Token, deadline.Token);
                var reply = await nodes.InvokeAsync(identity.UserId, identity.NodeId, Guid.NewGuid().ToString("N"),
                    PluginCapability.Id, PluginCapability.Version, PluginCapability.Prepare,
                    JsonSerializer.SerializeToElement(new { AllowNetwork = network.AllowSandboxNetworkAccess }), JsonSerializer.SerializeToElement(new { package }),
                    [UserPermissionKeys.Projects], preparation.Token).ConfigureAwait(false);
                if (reply.Failure is { } failure) { throw new InvalidOperationException(failure); }

                var tools = reply.Result?.Deserialize<string[]>() ?? [];
                var expectedRoles = plugin.ExecutionProviders.Where(item => item.Definition.SupportsNodeExecution).Select(item => "execution:" + item.Definition.Id);
                if (tools.Length == 0 || expectedRoles.Any(role => !tools.Contains(role, StringComparer.OrdinalIgnoreCase)))
                { throw new InvalidOperationException("The package did not expose its declared node-compatible tools or execution contributions."); }

                _states[key] = new(package, "ready", tools, null, device.Name);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Plugin {PluginId} could not prepare on node {NodeId}.", plugin.Manifest.Id, identity.NodeId);
                var failure = deadline.IsCancellationRequested ? "Package preparation exceeded the ten-minute limit." : ex.Message;
                _states[key] = new(null, "unavailable", [], failure, device.Name);
                if (previous?.Failure != failure)
                {
                    var operatorId = await permissions.GetOperatorIdAsync(ct).ConfigureAwait(false);
                    if (operatorId is not null)
                    {
                        await notifications.NotifyAsync(operatorId, new NotificationMessage(
                            Localize(operatorId, NotificationTextKeys.PluginSetupTitle, "Plugin setup needs attention"),
                            Localize(operatorId, NotificationTextKeys.PluginSetupBody,
                                "{0} could not be prepared on {1}. {2} Use Retry after correcting the setup.", plugin.Manifest.Name, device.Name, failure),
                            NotificationKind.PluginSetup, PluginId: plugin.Manifest.Id)).ConfigureAwait(false);
                    }

                    await notifications.NotifyAsync(identity.UserId, new NotificationMessage(plugin.Manifest.Name,
                        Localize(identity.UserId, NotificationTextKeys.PluginDeviceUnavailable,
                            "Temporarily unavailable on {0}. The operator has been notified.", device.Name), NotificationKind.Generic)).ConfigureAwait(false);
                }
            }
            finally { await PublishStatusAsync(identity.UserId, plugin.Manifest.Id, CancellationToken.None).ConfigureAwait(false); }
        }
    }

    private async Task PublishStatusAsync(string userId, string pluginId, CancellationToken ct)
    {
        var operatorId = await permissions.GetOperatorIdAsync(ct).ConfigureAwait(false);
        if (operatorId is null) { return; }

        var status = GetStatus(userId, isOperator: true).FirstOrDefault(item => item.PluginId == pluginId)
            ?? new NodePluginStatus(pluginId, [], [], []);
        await updates.NotifyAsync(operatorId, status).ConfigureAwait(false);
    }

    /// <summary>Admits a compatible tool only on the selected owned node with the current approved package.</summary>
    public async Task<JsonElement> InvokeAsync(string pluginId, RunContext run, ProjectDefinition project,
        AIFunction tool, AIFunctionArguments arguments, CancellationToken ct)
    {
        var key = PluginCapability.ConfigurationKey(pluginId);
        using var execution = executions.RegisterConfiguration(key, executions.GetConfigurationVersion(key), run.UserId, ct);
        var plugin = plugins.Plugins.FirstOrDefault(plugin => plugin.Manifest.Id == pluginId && activation.IsEnabled(pluginId))
            ?? throw new ToolFailureException("This plugin is disabled. Select an available tool.");
        var package = ReadPackage(plugin);
        if (!_states.TryGetValue((run.UserId, project.NodeId!, pluginId), out var status) ||
            status.State != "ready" || status.Package != package || !status.Tools.Contains(tool.Name, StringComparer.Ordinal))
        {
            throw new ToolFailureException("This plugin is temporarily unavailable on the project device.",
                "Check its setup status in Plugins. Setup failures are reported to the operator.");
        }

        var context = new ProjectNodeContext(project.Id, project.RootPath, run.CurrentAgentId, run.ExecutionProviderId, run.AirGapped);
        var reply = await nodes.InvokeAsync(run.UserId, project.NodeId!, run.ExecutionId, PluginCapability.Id,
            PluginCapability.Version, PluginCapability.Invoke, JsonSerializer.SerializeToElement(context),
            JsonSerializer.SerializeToElement(new { package, tool = tool.Name, arguments = arguments.ToDictionary() }),
            [UserPermissionKeys.Projects], execution.Token).ConfigureAwait(false);
        return results.StoreReply(reply);
    }

    /// <inheritdoc />
    public void RequireExecutionReady(string userId, string nodeId, string? providerId)
    {
        if (string.Equals(providerId, "host", StringComparison.OrdinalIgnoreCase)) { return; }

        var plugin = plugins.Plugins.FirstOrDefault(plugin => activation.IsEnabled(plugin.Manifest.Id) &&
            plugin.ExecutionProviders.Any(item => string.Equals(item.Definition.Id, providerId, StringComparison.OrdinalIgnoreCase)));
        if (plugin is null || !_states.TryGetValue((userId, nodeId, plugin.Manifest.Id), out var state) ||
            state.State != "ready" || state.Package != ReadPackage(plugin) ||
            !state.Tools.Contains("execution:" + providerId, StringComparer.OrdinalIgnoreCase))
        {
            throw new ToolFailureException("The execution environment is unavailable on the project device.",
                "Ask the operator to check its setup status in Plugins and retry after correcting the prerequisites.");
        }
    }

    private static NodePluginPackage ReadPackage(LoadedAgentPlugin plugin)
    {
        var inspection = AgentPluginLoader.InspectDirectory(plugin.PluginDirectory).SingleOrDefault(entry => entry.Manifest?.Id == plugin.Manifest.Id);
        var package = InstalledPluginPackage.Read(inspection)
            ?? throw new InvalidOperationException("Reinstall this plugin from a NuGet package before using it on devices.");
        return new(plugin.Manifest.Id, package.PackageId, package.Version);
    }

    private sealed record Deployment(NodePluginPackage? Package, string State, IReadOnlyList<string> Tools, string? Failure, string DeviceName);

    private string Localize(string userId, string key, string fallback, params object[] args) =>
        text.Localize(users.TryGetExistingScope(userId, out var scope) ? scope.UiSettings.CultureName : UiCultureCatalog.DefaultCultureName,
            key, fallback, args);
}
