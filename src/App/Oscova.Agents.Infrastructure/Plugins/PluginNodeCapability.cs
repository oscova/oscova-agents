using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Collections;
using Oscova.Agents.Core.ApiContracts.Nodes;
using Oscova.Agents.Core.Auth;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Nodes;
using Oscova.Agents.Core.Projects;
using Oscova.Agents.Infrastructure.Plugins.Registry;
using Oscova.Agents.Settings;
using Oscova.Agents.Infrastructure.Projects;
using Oscova.Agents.Infrastructure.Updates;
using Oscova.Agents.Infrastructure.Skills;
using Oscova.Agents.Execution;
using Oscova.Agents.Middleware;
using Oscova.Agents.Plugins;
using Oscova.Agents.Toolsets;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Infrastructure.Plugins;

/// <summary>Installs exact plugin packages and invokes their explicitly compatible contributions on a node.</summary>
public sealed class PluginNodeCapability(string userId, IReadOnlyList<string> privateRoots,
    ILoggerFactory logs, ExecutionEnvironmentCatalog? environments = null) : INodeCapability
{
    private readonly SemaphoreSlim _installation = new(1, 1);
    private readonly Dictionary<NodePluginPackage, LoadedTools> _loaded = [];
    private readonly RunContextAccessor _runs = new();
    private readonly NodeToolOutputStore _output = new();

    public NodeCapability Description { get; } = new(PluginCapability.Id, PluginCapability.Version,
        [new(PluginCapability.Prepare, "Prepare an approved package.", JsonSerializer.SerializeToElement(new { type = "object" })),
         new(PluginCapability.Invoke, "Invoke a prepared project tool.", JsonSerializer.SerializeToElement(new { type = "object" }))]);

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task CleanupExecutionAsync(string executionId, CancellationToken ct) => Task.CompletedTask;
    public Task ResetAsync() => Task.CompletedTask;
    public IReadOnlyList<string> GetRequiredPermissions(string operation) => operation switch
    {
        PluginCapability.Prepare or PluginCapability.Invoke => [UserPermissionKeys.Projects],
        _ => throw new InvalidOperationException("The plugin operation is unsupported.")
    };

    /// <summary>Accepts only exact package identities from the authenticated server's admitted invocation.</summary>
    public async Task<NodeReply> InvokeAsync(NodeInvocation invocation, CancellationToken ct, NodeCallbackHandler? callback = null)
    {
        var package = invocation.Arguments.GetProperty("package").Deserialize<NodePluginPackage>()
            ?? throw new ArgumentException("Plugin package identity is required.");
        await _installation.WaitAsync(ct).ConfigureAwait(false);
        LoadedTools loaded;
        try
        {
            if (invocation.Tool == PluginCapability.Prepare && !_loaded.ContainsKey(package))
            {
                // Reconnection drains calls before replacing the same plugin's exact package.
                foreach (var previous in _loaded.Keys.Where(previous => string.Equals(previous.PluginId, package.PluginId, StringComparison.OrdinalIgnoreCase)).ToArray())
                {
                    var retired = _loaded[previous];
                    _loaded.Remove(previous);
                    await DisposeToolsAsync(retired.Instances).ConfigureAwait(false);
                }

                if (environments is not null) { await environments.RemovePluginAsync(package.PluginId).ConfigureAwait(false); }

                loaded = await InstallAsync(package, ct).ConfigureAwait(false);
                _loaded.Add(package, loaded);
            }

            loaded = _loaded.GetValueOrDefault(package)
                ?? throw new InvalidOperationException("The plugin is not prepared on this device.");
        }
        finally { _installation.Release(); }

        if (invocation.Tool == PluginCapability.Prepare)
        {
            var allowNetwork = invocation.Context.TryGetProperty("AllowNetwork", out var network) && network.GetBoolean();
            foreach (var role in loaded.Roles.Where(role => role.StartsWith("execution:", StringComparison.Ordinal)))
            {
                var id = role["execution:".Length..];
                var definition = environments!.Find(id) ?? throw new InvalidOperationException("The execution provider is unavailable on this device.");
                var ready = await environments.GetProvider(id).CheckAvailabilityAsync(new ExecutionAvailabilityRequest(allowNetwork,
                    definition.UsesHostPaths ? ShellExecutorFactory.CreateHostEnvironment() : new Dictionary<string, string?>()), ct).ConfigureAwait(false);
                if (!ready.IsAvailable) { throw new InvalidOperationException(ready.Reason); }
            }

            return new(JsonSerializer.SerializeToElement(loaded.Tools.Select(tool => tool.Name).Concat(loaded.Roles).ToArray()), null, []);
        }

        if (invocation.Tool != PluginCapability.Invoke) { throw new ArgumentException("The plugin operation is unsupported."); }

        var context = invocation.Context.Deserialize<ProjectNodeContext>()
            ?? throw new ArgumentException("Project context is required.");
        var root = ProjectRootValidator.ValidateNode(context.RootPath, privateRoots);
        IReadOnlyList<AllowedDirectory> directories = [new(root.Path, "project", DirectorySourceKind.LocalPath,
            WorkspaceAccessMode.ReadWrite, AgentPath: "/project", IsWorkingDirectory: true)];
        await using var scope = _runs.SetContext(new RunContext
        {
            UserId = userId, ProjectId = context.ProjectId, ExecutionId = invocation.ExecutionId,
            ExecutionKind = RunExecutionKind.WebChat,
            CurrentAgentId = context.AgentId, AllowedDirectories = directories, StandardRunDirectories = directories,
            ExecutionProviderId = context.ProviderId, AirGapped = context.AirGapped
        });
        var tool = loaded.Tools.Find(invocation.Arguments.GetProperty("tool").GetString()!)
            ?? throw new InvalidOperationException("The requested plugin tool is not available on this device.");
        var values = invocation.Arguments.GetProperty("arguments").Deserialize<Dictionary<string, object?>>()
            ?? throw new ArgumentException("Tool arguments must be an object.");
        var arguments = new AIFunctionArguments(values);
        WorkspacePathValidationMiddleware.ValidateWorkspaceArguments(tool, arguments, directories);
        _output.Begin();
        try
        {
            var result = await tool.InvokeAsync(arguments, ct).ConfigureAwait(false);
            var reply = new NodeReply(JsonSerializer.SerializeToElement(result, tool.JsonSerializerOptions), null, _output.Finish());
            NodeProtocol.ValidateSize(reply);
            return reply;
        }
        finally { _output.Finish(); }
    }

    private async Task<LoadedTools> InstallAsync(NodePluginPackage package, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(package.Version)) { throw new ArgumentException("An exact package version is required."); }
        // Separate package versions keep active native assemblies out of the installer's replacement path.
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(package.PackageId + ":" + package.Version)));
        var root = Path.Combine(privateRoots[0], "plugins", identity);
        var inspection = AgentPluginLoader.InspectDirectory(root).SingleOrDefault(entry => entry.Manifest?.Id == package.PluginId);
        var installed = InstalledPluginPackage.Read(inspection);
        if (installed?.PackageId != package.PackageId || installed.Version != package.Version)
        {
            var installer = new NuGetPluginInstallService(logs.CreateLogger<NuGetPluginInstallService>(),
                NuGetApplicationUpdateSource.OfficialFeedUrl, root);
            await installer.InstallAsync(package.PackageId, package.Version, package.PluginId, ct).ConfigureAwait(false);
        }

        inspection = AgentPluginLoader.InspectDirectory(root).SingleOrDefault(entry => entry.Manifest?.Id == package.PluginId);
        var reason = PluginFrameworkCompatibility.GetIncompatibility(inspection?.Manifest?.FrameworkVersionRange, PluginFrameworkCompatibility.CurrentVersion);
        if (reason is not null) { throw new InvalidOperationException(reason); }

        var catalog = AgentPluginLoader.LoadDirectory(root, new HashSet<string>([package.PluginId], StringComparer.OrdinalIgnoreCase));
        var plugin = catalog.Plugins.SingleOrDefault()
            ?? throw new InvalidOperationException("The installed plugin could not load. Check the node log and package dependencies.");
        var services = new NodeServices(_output);
        var context = new AgentPluginContext(services, services, logs, TimeProvider.System,
            plugin.PluginDirectory, Path.Combine(privateRoots[0], "models"))
        {
            GetAllowedDirectories = () => _runs.Current?.AllowedDirectories ?? []
        };
        var runtimeCatalog = environments;
        runtimeCatalog?.AddPlugins(catalog, owned => new AgentPluginContext(services, services, logs, TimeProvider.System,
                owned.PluginDirectory, Path.Combine(privateRoots[0], "models"))
            {
                PrivateDataDirectory = Path.Combine(privateRoots[0], "plugin-data", owned.Manifest.Id.ToLowerInvariant()),
                InstallationSettings = new BoundPluginSettings(new FileSettingsRepository(_ => Path.Combine(privateRoots[0], "settings")),
                    owned.Manifest.Id, Oscova.Agents.Core.WellKnownUsers.SystemId, (write, _) => write ? Task.FromException(new UnauthorizedAccessException("Installation settings writes require an interactive operator context.")) : Task.CompletedTask)
            }, logs.CreateLogger<ExecutionEnvironmentCatalog>());
        var tools = new AgentToolCollection();
        var instances = new List<IAgentToolset>();
        try
        {
            foreach (var contribution in plugin.Toolsets.Where(contribution => contribution.SupportsNodeExecution))
            {
                var instance = contribution.Factory(context);
                instances.Add(instance);
                tools.Add(instance, package.PluginId);
            }

            var roles = plugin.ExecutionProviders.Where(item => item.Definition.SupportsNodeExecution && runtimeCatalog?.Find(item.Definition.Id) is not null)
                .Select(item => "execution:" + item.Definition.Id).ToList();
            if (plugin.ContainerLaunchers.Count > 0 && runtimeCatalog?.ContainerConfigurationKey() == PluginCapability.ConfigurationKey(plugin.Manifest.Id)) { roles.Add("container-launch"); }

            if (tools.Count == 0 && roles.Count == 0) { throw new InvalidOperationException("The plugin supplies no node-compatible tools or execution contributions."); }

            return new(tools, instances, roles);
        }
        catch
        {
            await DisposeToolsAsync(instances).ConfigureAwait(false);
            if (runtimeCatalog is not null) { await runtimeCatalog.RemovePluginAsync(plugin.Manifest.Id).ConfigureAwait(false); }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var loaded in _loaded.Values) { await DisposeToolsAsync(loaded.Instances).ConfigureAwait(false); }

        _installation.Dispose();
    }

    private static async Task DisposeToolsAsync(IEnumerable<IAgentToolset> instances)
    {
        foreach (var instance in instances)
        {
            if (instance is IAsyncDisposable asyncDisposable) { await asyncDisposable.DisposeAsync().ConfigureAwait(false); }
            else if (instance is IDisposable disposable) { disposable.Dispose(); }
        }
    }

    private sealed record LoadedTools(AgentToolCollection Tools, IReadOnlyList<IAgentToolset> Instances, IReadOnlyList<string> Roles);

    // Node contributions have no implicit access to the central account's credentials or host services.
    private sealed class NodeServices(IToolOutputStore output) : IAgentPluginServices, IToolCredentialResolver
    {
        public IPluginEncryptionService Encryption => throw new NotSupportedException("Account encryption is available on the server.");
        public IToolOutputStore ToolOutputStore => output;
        public HttpClient CreateHttpClient() => throw new NotSupportedException("This node does not supply plugin HTTP services.");
        public HttpMessageHandler CreateHttpMessageHandler() => throw new NotSupportedException("This node does not supply plugin HTTP services.");
        public Task EnsureDownloadAsync(PluginDownloadRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Asset downloads are available on the server.");
        public Task<string?> ResolveAccessTokenAsync(string providerId, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<string?> ResolveAsync(string credentialKey, string fieldName, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> ResolveAsync(string credentialKey, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }
}
