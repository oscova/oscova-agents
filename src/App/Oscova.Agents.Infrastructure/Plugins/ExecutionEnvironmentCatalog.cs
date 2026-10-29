using Microsoft.Extensions.Logging;
using Oscova.Agents.Execution;
using Oscova.Agents.Plugins;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Core.Nodes;
using Oscova.Agents.Infrastructure.Skills;

namespace Oscova.Agents.Infrastructure.Plugins;

/// <summary>Admits device execution runtimes from the existing active plugin catalog.</summary>
public sealed class ExecutionEnvironmentCatalog : IExecutionEnvironmentCatalog, IAsyncDisposable
{
    private readonly Dictionary<string, (ExecutionEnvironmentDefinition Definition, IExecutionEnvironmentProvider Provider, LoadedAgentPlugin? Plugin)> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(IContainerProcessLauncher Launcher, LoadedAgentPlugin Plugin)> _launchers = [];
    private readonly Lock _gate = new();
    private readonly bool _forNode;
    private readonly PluginContributionLifetime _lifetime;
    private readonly PluginActivationSettings? _activation;

    /// <summary>Creates the local built-in catalog and activates each unambiguous plugin contribution once.</summary>
    public ExecutionEnvironmentCatalog(IAgentPluginCatalog plugins, PluginContextFactory contexts,
        ILogger<ExecutionEnvironmentCatalog> logger, PluginActivationSettings activation)
        : this(logger)
    {
        _activation = activation;
        AddPlugins(plugins, plugin => contexts.Create(plugin, EmptyCredentials.Instance, TimeProvider.System), logger);
    }

    /// <summary>Creates a device catalog with its built-in Host provider before optional packages are prepared.</summary>
    public ExecutionEnvironmentCatalog(ILogger logger, bool forNode = false)
    {
        _lifetime = new(logger);
        _forNode = forNode;
        _entries.Add("host", (HostExecutionProvider.Definition, new HostExecutionProvider(), null));
    }

    /// <summary>Admits packages prepared through the device's existing installation path.</summary>
    public void AddPlugins(IAgentPluginCatalog plugins, Func<LoadedAgentPlugin, AgentPluginContext> contextFactory, ILogger logger)
    {
        // Device package preparation can overlap metadata reads from project and MCP calls.
        lock (_gate)
        {
            var groups = plugins.Plugins.SelectMany(plugin => plugin.ExecutionProviders.Where(item => !_forNode || item.Definition.SupportsNodeExecution).Select(item => (Plugin: plugin, Item: item)))
                .GroupBy(entry => entry.Item.Definition.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups)
            {
                if (string.IsNullOrWhiteSpace(group.Key) || group.Count() != 1 || _entries.ContainsKey(group.Key))
                {
                    if (_entries.TryGetValue(group.Key, out var existing) && existing.Plugin is not null)
                    { Reject(existing.Plugin, $"Execution provider '{group.Key}' is duplicated."); _entries.Remove(group.Key); }

                    foreach (var rejected in group) { Reject(rejected.Plugin, $"Execution provider '{group.Key}' is reserved or duplicated."); }

                    continue;
                }

                var entry = group.Single();
                try
                {
                    var runtime = entry.Item.Factory(contextFactory(entry.Plugin))
                        ?? throw new InvalidOperationException("The execution provider factory returned null.");
                    _lifetime.Add(runtime);
                    _entries.Add(group.Key, (entry.Item.Definition, runtime, entry.Plugin));
                }
                catch (Exception ex) { logger.LogError(ex, "Execution provider {ProviderId} could not activate.", group.Key); Reject(entry.Plugin, ex.Message); }
            }

            foreach (var plugin in plugins.Plugins)
            {
                foreach (var contribution in plugin.ContainerLaunchers)
                {
                    try
                    {
                        var launcher = contribution.Factory(contextFactory(plugin))
                            ?? throw new InvalidOperationException("The container launch factory returned null.");
                        if (!_forNode || launcher.SupportsNodeExecution) { _launchers.Add((launcher, plugin)); }

                        _lifetime.Add(launcher);
                    }
                    catch (Exception ex) { logger.LogError(ex, "Plugin {PluginId} container launcher could not activate.", plugin.Manifest.Id); Reject(plugin, ex.Message); }
                }
            }

            void Reject(LoadedAgentPlugin plugin, string message) => plugins.ReportDiagnostic(new(
                AgentPluginLoadDiagnosticLevel.Warning, plugin.PluginDirectory, message));
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ExecutionEnvironmentDefinition> Environments
    { get { lock (_gate) { return _entries.Values.Where(Active).Select(entry => entry.Definition).ToArray(); } } }
    private bool Active((ExecutionEnvironmentDefinition Definition, IExecutionEnvironmentProvider Provider, LoadedAgentPlugin? Plugin) entry)
        => entry.Plugin is null || _activation?.IsEnabled(entry.Plugin.Manifest.Id) != false;
    /// <inheritdoc />
    public ExecutionEnvironmentDefinition? Find(string? id)
    { lock (_gate) { return id is not null && _entries.TryGetValue(id, out var entry) && Active(entry) ? entry.Definition : null; } }
    /// <summary>Resolves the owning plugin for presentation and settings association.</summary>
    public LoadedAgentPlugin? Owner(string id)
    { lock (_gate) { return _entries.TryGetValue(id, out var entry) ? entry.Plugin : null; } }
    /// <inheritdoc />
    public IExecutionEnvironmentProvider GetProvider(string id)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(id, out var entry) && Active(entry) ? entry.Provider
                : throw new InvalidOperationException($"Execution environment '{id}' is unavailable on this device. Enable its plugin and restart, or save another environment in Execution settings.");
        }
    }
    /// <inheritdoc />
    public IContainerProcessLauncher GetContainerLauncher() => ActiveLaunchers().SingleOrDefault().Launcher
        ?? throw new InvalidOperationException("Containerized MCP is unavailable. Install and enable exactly one container execution plugin on this device, then restart.");
    private IReadOnlyList<(IContainerProcessLauncher Launcher, LoadedAgentPlugin Plugin)> ActiveLaunchers()
    {
        lock (_gate)
        {
            var active = _launchers.Where(entry => _activation?.IsEnabled(entry.Plugin.Manifest.Id) != false).ToArray();
            if (active.Length > 1) { throw new InvalidOperationException("Containerized MCP has multiple launchers. Enable only one container launch plugin and restart."); }

            return active;
        }
    }
    /// <summary>Names the existing plugin configuration dependency of an admitted execution provider.</summary>
    public string? ConfigurationKey(string id) => Owner(id) is { } plugin ? PluginCapability.ConfigurationKey(plugin.Manifest.Id) : null;
    /// <summary>Names the existing plugin configuration dependency of the single admitted container launcher.</summary>
    public string? ContainerConfigurationKey() => ActiveLaunchers().SingleOrDefault().Plugin is { } plugin ? PluginCapability.ConfigurationKey(plugin.Manifest.Id) : null;
    /// <summary>Stops shared provider work for a changed configuration, or all providers when a device disconnects.</summary>
    public Task StopAsync(string? configurationKey = null)
    {
        IExecutionEnvironmentProvider[] providers;
        lock (_gate)
        {
            providers = _entries.Values.Where(entry => configurationKey is null ||
                entry.Plugin is not null && PluginCapability.ConfigurationKey(entry.Plugin.Manifest.Id) == configurationKey)
                .Select(entry => entry.Provider).Distinct().ToArray();
        }

        return Task.WhenAll(providers.Select(provider => provider.StopAsync()));
    }
    /// <summary>Retires one plugin's runtimes before its exact replacement package is prepared.</summary>
    public async Task RemovePluginAsync(string pluginId)
    {
        IExecutionEnvironmentProvider[] providers;
        object[] contributions;
        lock (_gate)
        {
            var entries = _entries.Where(entry => string.Equals(entry.Value.Plugin?.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase)).ToArray();
            providers = entries.Select(entry => entry.Value.Provider).Distinct().ToArray();
            contributions = [.. providers, .. _launchers.Where(entry => string.Equals(entry.Plugin.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.Launcher)];
            foreach (var entry in entries) { _entries.Remove(entry.Key); }

            _launchers.RemoveAll(entry => string.Equals(entry.Plugin.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        }

        try { await Task.WhenAll(providers.Select(provider => provider.StopAsync())).ConfigureAwait(false); }
        finally { await _lifetime.ReleaseAsync(contributions).ConfigureAwait(false); }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _lifetime.DisposeAsync();
    private sealed class EmptyCredentials : IToolCredentialResolver
    {
        public static EmptyCredentials Instance { get; } = new();
        public Task<string?> ResolveAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> ResolveAsync(string key, string field, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }
}
