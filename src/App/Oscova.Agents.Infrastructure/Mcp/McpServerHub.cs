using Oscova.Agents.Core.Auth;
using Oscova.Agents.Core.Nodes;
using Oscova.Agents.Core.Projects;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Collections;

using Microsoft.Extensions.AI;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Mcp;
using Oscova.Agents.Execution;
using Oscova.Agents.Infrastructure.Plugins;
using Oscova.Agents.Toolsets;
using Oscova.Agents.Runtime;
using Oscova.Agents.Utils;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Infrastructure.Mcp;

public sealed class McpServerHub : IMcpServerHub
{
    /// <summary>The MCP function property holding its <see cref="ExternalToolMetadata"/>, which a run keeps after the server disconnects.</summary>
    public const string ToolMetadataProperty = "oscova.mcpTool";

    private readonly ILogger<McpServerHub> _logger;
    private readonly IElicitationRouter _elicitationRouter;
    private readonly RunContextAccessor _runContextAccessor;
    private readonly string _userId;
    private readonly IUserDirectoryService _userDirectoryService;
    private readonly IExecutionEnvironmentCatalog _executionEnvironments;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IToolOutputStore _outputStore;
    private readonly TimeSpan _stdioShutdownTimeout;
    private readonly Lock _clientLock = new();

    private readonly Dictionary<Guid, McpClient> _clients = [];
    private readonly Dictionary<Guid, List<AIFunction>> _serverTools = [];
    private readonly List<AIFunction> _tools = [];
    private readonly Dictionary<string, ExternalToolMetadata> _toolMetadata =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<AIFunction> Tools
    {
        get
        {
            lock (_clientLock)
            {
                return _tools.Concat(_projectHubs.Values.SelectMany(hub => hub.Tools))
                    .DistinctBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }
    }

    public IReadOnlyDictionary<string, ExternalToolMetadata> ToolMetadata
    {
        get
        {
            lock (_clientLock)
            {
                return _toolMetadata.Concat(_projectHubs.Values.SelectMany(hub => hub.ToolMetadata))
                    .DistinctBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <inheritdoc />
    public Action? ToolCollectionChanged { get; set; }

    public McpServerHub(
        ILogger<McpServerHub> logger,
        IElicitationRouter elicitationRouter,
        RunContextAccessor runContextAccessor,
        string userId,
        IUserDirectoryService userDirectoryService,
        IExecutionEnvironmentCatalog executionEnvironments,
        IHttpClientFactory httpClientFactory,
        IToolOutputStore outputStore,
        TimeSpan stdioShutdownTimeout)
    {
        _logger = logger;
        _elicitationRouter = elicitationRouter;
        _runContextAccessor = runContextAccessor;
        _userId = userId;
        _userDirectoryService = userDirectoryService;
        _executionEnvironments = executionEnvironments;
        _httpClientFactory = httpClientFactory;
        _outputStore = outputStore;
        _stdioShutdownTimeout = stdioShutdownTimeout;

    }

    private async ValueTask<ElicitResult> HandleElicitationAsync(
        ConnectionCalls calls, ElicitRequestParams? requestParams, CancellationToken token)
    {
        var call = Volatile.Read(ref calls.Current);
        if (requestParams is null || call?.Context is null || call.Token.IsCancellationRequested)
        {
            return new ElicitResult { Action = "cancel" };
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, call.Token);
        try
        {
            var result = await _elicitationRouter.RouteElicitationAsync(
                requestParams, call.Context, cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            return ReferenceEquals(call, Volatile.Read(ref calls.Current))
                ? result : new ElicitResult { Action = "cancel" };
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return new ElicitResult { Action = "cancel" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MCP input routing failed for user {UserId}.", _userId);
            return new ElicitResult { Action = "cancel" };
        }
    }

    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, McpServerHub> _projectHubs = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, ConnectionCalls> _calls = [];
    private readonly CancellationTokenSource _stopping = new();
    private McpToolSettings? _settings;
    private ExecutionPermissionRegistry? _executions;
    private IUserPermissionService? _permissions;
    private INodeDispatcher? _nodes;
    private ProjectAccessService? _projects;
    private ILoggerFactory? _logs;
    private ProjectDefinition? _project;
    private bool _airGapped;
    private Task? _reaper;

    /// <summary>Connects retained MCP processes to the existing account and execution-policy owners.</summary>
    public void BindGovernance(McpToolSettings settings, ExecutionPermissionRegistry executions,
        IUserPermissionService permissions, INodeDispatcher nodes, ProjectAccessService projects, ILoggerFactory logs)
    {
        _settings = settings;
        _executions = executions;
        _permissions = permissions;
        _nodes = nodes;
        _projects = projects;
        _logs = logs;
        executions.ConfigurationChanged += ConfigurationChangedAsync;
        executions.Changed += PermissionsChangedAsync;
        _reaper = ReapIdleAsync();
    }

    /// <summary>Builds one conversation's MCP view without replacing another project's tools.</summary>
    public async Task<AgentToolCollection?> GetRunToolsAsync(AgentToolCollection baseline, RunContext? run, CancellationToken ct,
        IReadOnlyCollection<string>? requiredServerQualifiers = null)
    {
        if (_settings is null || _settings.GetAvailable().Count == 0) { return null; }

        if (run is not null && run.UserId != _userId) { throw new UnauthorizedAccessException("MCP account mismatch."); }

        var project = run?.ProjectId is { } id ? await _projects!.AuthorizeAsync(id, ct).ConfigureAwait(false) : null;
        var key = $"{project?.Id ?? "workspace"}/{run?.AirGapped == true}";
        await _connectionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_projectHubs.TryGetValue(key, out var hub))
            {
                hub = new McpServerHub(_logger, _elicitationRouter, _runContextAccessor, _userId,
                    _userDirectoryService, _executionEnvironments, _httpClientFactory, _outputStore, _stdioShutdownTimeout)
                {
                    _settings = _settings, _executions = _executions, _permissions = _permissions,
                    _nodes = _nodes, _projects = _projects, _logs = _logs, _project = project,
                    _airGapped = run?.AirGapped == true,
                    ToolCollectionChanged = () => ToolCollectionChanged?.Invoke()
                };
                _projectHubs[key] = hub;
            }

            foreach (var server in _settings.GetAvailable().Where(server => server.IsEnabled))
            {
                if (!hub.IsServerTracked(server.Id) && hub.CanUse(server))
                {
                    var result = await hub.ConnectServerInternalAsync(server, ct).ConfigureAwait(false);
                    // Optional connections can fail without preventing an unrelated agent from starting.
                    var qualifier = ToolNaming.NormalizeQualifier(server.Name);
                    if (!result.IsConnected && requiredServerQualifiers?.Contains(qualifier, StringComparer.OrdinalIgnoreCase) == true)
                    {
                        throw new McpConnectionException(server.Id.ToString(), result.Error);
                    }
                }
            }

            var tools = baseline.Clone();
            // Only published MCP functions belong to this hub, including hashed wire names.
            var mcpNames = ToolMetadata.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var tool in tools.ToArray())
            {
                if (mcpNames.Contains(tool.Name)) { tools.Remove(tool); }
            }

            foreach (var tool in hub.Tools)
            {
                var metadata = hub.ToolMetadata[tool.Name];
                tools.Add(tool, metadata.ServerName, metadata.OriginalName, metadata.NetworkScope);
            }

            ToolCollectionChanged?.Invoke();
            return tools;
        }
        finally { _connectionGate.Release(); }
    }

    private bool CanUse(McpServerConfig server)
    {
        if (!server.IsEnabled) { return false; }

        return server.NodeId is null || server.NodeId == _project?.NodeId;
    }

    private async Task<IClientTransport> CreateTransportAsync(McpServerConfig server, CancellationToken ct, bool setup = false)
    {
        if (_settings is not null)
        {
            var definition = _settings.GetAvailable().FirstOrDefault(candidate => candidate.Id == server.Id);
            if (definition?.IsEnabled != true || !setup && !CanUse(definition))
            {
                throw new UnauthorizedAccessException("This MCP connection is not enabled for the selected environment.");
            }

            server = definition.Clone();
            if (!await _permissions!.IsOperatorAsync(_userId, ct).ConfigureAwait(false) &&
                _settings.McpServerConfigs.Any(candidate => candidate.Id == server.Id) &&
                server.Transport == McpTransportType.Stdio && server.NodeId is null)
            {
                throw new UnauthorizedAccessException("Personal MCP programs can run only on an authorized node.");
            }
        }

        if (server.Transport == McpTransportType.Stdio && server.ExecutionEnvironment == McpExecutionEnvironment.Containerized)
        {
            // A prepared node package does not grant authority after its server plugin is disabled.
            _executionEnvironments.GetContainerLauncher();
        }

        if (server.NodeId is not null)
        {
            return new NodeMcpClientTransport(_nodes!, _userId, _project, server, _airGapped, _logs!);
        }

        var local = server.Clone();
        if (local.Transport == McpTransportType.Stdio && string.IsNullOrWhiteSpace(local.WorkingDirectory))
        {
            local.WorkingDirectory = _userDirectoryService.GetWorkspaceDirectoryPath(_userId, UserFileNames.DefaultWorkspaceDirectory);
        }

        return await McpTransportFactory.CreateAsync(local, BuildWorkspaceMounts(), _executionEnvironments,
            _httpClientFactory, _logger, _stdioShutdownTimeout, ct).ConfigureAwait(false);
    }

    private async Task ConfigurationChangedAsync(string key)
    {
        foreach (var hub in _projectHubs.Values.Prepend(this))
        {
            Guid[] containerConnections;
            lock (hub._clientLock) { containerConnections = hub._calls.Where(entry => entry.Value.ContainerKey == key).Select(entry => entry.Key).ToArray(); }

            foreach (var id in containerConnections) { await hub.RemoveServerAsync(id, CancellationToken.None).ConfigureAwait(false); }
        }

        if (_settings?.GetAvailable().Any(server => _settings.GetResourceKey(server.Id) == key) != true) { return; }

        await _connectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var server in _settings.GetAvailable().Where(server => _settings.GetResourceKey(server.Id) == key))
            {
                await RemoveServerAsync(server.Id, CancellationToken.None).ConfigureAwait(false);
                foreach (var hub in _projectHubs.Values) { await hub.RemoveServerAsync(server.Id, CancellationToken.None).ConfigureAwait(false); }
            }
        }
        finally { _connectionGate.Release(); }

        ToolCollectionChanged?.Invoke();
    }

    private async Task PermissionsChangedAsync(string userId, IReadOnlyList<string> granted)
    {
        if (userId != _userId || granted.Contains(UserPermissionKeys.Projects)) { return; }

        await _connectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var hub in _projectHubs.Values.Where(hub => hub._project is not null))
            {
                await hub.DisposeAllClientsAsync().ConfigureAwait(false);
            }
        }
        finally { _connectionGate.Release(); }
    }

    private async Task ReapIdleAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_stopping.Token).ConfigureAwait(false))
            {
                await _connectionGate.WaitAsync(_stopping.Token).ConfigureAwait(false);
                try
                {
                    foreach (var hub in _projectHubs.Values.Prepend(this))
                    {
                        KeyValuePair<Guid, ConnectionCalls>[] calls;
                        lock (hub._clientLock) { calls = hub._calls.ToArray(); }

                        foreach (var pair in calls)
                        {
                            if (!await pair.Value.Gate.WaitAsync(0, _stopping.Token).ConfigureAwait(false)) { continue; }

                            try
                            {
                                if (DateTimeOffset.UtcNow - pair.Value.LastUsed >= TimeSpan.FromMinutes(30))
                                {
                                    await hub.RemoveServerAsync(pair.Key, _stopping.Token).ConfigureAwait(false);
                                }
                            }
                            finally { pair.Value.Gate.Release(); }
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "MCP idle cleanup failed for user {UserId}.", _userId);
                }
                finally { _connectionGate.Release(); }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
    }

    public async Task LoadToolsAsync(IEnumerable<McpServerConfig> servers, CancellationToken cancellationToken)
    {
        await DisposeAllClientsAsync().ConfigureAwait(false);

        var list = servers?.ToList() ?? [];

        if (list.Count == 0)
        {
            _logger.LogDebug("No MCP servers configured in settings.");
            return;
        }

        foreach (var server in list)
        {
            if (_settings is not null && !CanUse(server)) { continue; }

            await ConnectServerInternalAsync(server, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<McpServerConnectionResult> AddServerAsync(McpServerConfig server, CancellationToken cancellationToken)
        => ConnectServerInternalAsync(server, cancellationToken);

    public async Task RemoveServerAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var client = DetachServer(serverId);
        if (client is not null) { await client.DisposeAsync().ConfigureAwait(false); }
    }

    public async Task<McpServerConnectionResult> RestartServerAsync(McpServerConfig server, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await ConnectServerInternalAsync(server, cancellationToken).ConfigureAwait(false);
    }

    private async Task<McpServerConnectionResult> ConnectServerInternalAsync(McpServerConfig server, CancellationToken cancellationToken)
    {
        // Replacement owns the connection until discovery and publication have both completed.
        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ConnectServerCoreAsync(server, cancellationToken).ConfigureAwait(false); }
        finally { _connectionGate.Release(); }
    }

    private async Task<McpServerConnectionResult> ConnectServerCoreAsync(McpServerConfig server, CancellationToken cancellationToken)
    {
        using var userScope = BeginUserScope();
        if (!server.IsValidForConnect())
        {
            _logger.LogWarning("Skipping MCP server '{McpServerId}' due to invalid configuration.", server.Id);
            return new McpServerConnectionResult
            {
                ServerId = server.Id,
                ServerName = server.Name,
                Error = "Server configuration is incomplete."
            };
        }

        if (IsServerTracked(server.Id))
        {
            await RemoveServerAsync(server.Id, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var resourceKey = _settings?.GetResourceKey(server.Id);
            var version = resourceKey is null ? 0 : _executions!.GetConfigurationVersion(resourceKey);
            using var admission = resourceKey is null ? null :
                _executions!.RegisterConfiguration(resourceKey, version, _userId, cancellationToken);
            cancellationToken = admission?.Token ?? cancellationToken;
            var containerKey = server.Transport == McpTransportType.Stdio && server.ExecutionEnvironment == McpExecutionEnvironment.Containerized
                && _executionEnvironments is ExecutionEnvironmentCatalog catalog ? catalog.ContainerConfigurationKey() : null;
            var containerVersion = containerKey is null || _executions is null ? 0 : _executions.GetConfigurationVersion(containerKey);
            using var containerAdmission = containerKey is null || _executions is null ? null :
                _executions.RegisterConfiguration(containerKey, containerVersion, _userId, cancellationToken);
            cancellationToken = containerAdmission?.Token ?? cancellationToken;
            var setup = server.NodeId is not null && _project is null;
            var transport = await CreateTransportAsync(server, cancellationToken, setup).ConfigureAwait(false);
            var calls = new ConnectionCalls
            {
                ResourceKey = resourceKey, Version = version,
                ContainerKey = containerKey, ContainerVersion = containerVersion,
                Destination = server.NodeId is not null
                    ? $"Device {server.NodeId}" : "Server"
            };
            var options = new McpClientOptions
            {
                ClientInfo = new() { Name = "Oscova.McpClient", Version = "1.0.0" },
                Handlers = new() { ElicitationHandler = (request, token) => HandleElicitationAsync(calls, request, token) }
            };
            var client = await McpClient.CreateAsync(transport, options, cancellationToken: cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("MCP server '{ConfigName}' identifies itself as '{ServerInfoName}'.", server.Name, GetServerInfoName(client));
            lock (_clientLock)
            {
                _clients[server.Id] = client;
                _calls[server.Id] = calls;
            }

            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var registrations = new List<(AIFunction Tool, ExternalToolMetadata Metadata)>();
            foreach (var tool in tools)
            {
                var metadata = new ExternalToolMetadata(server.Name, tool.Name, server.NetworkScope, server.Id.ToString());
                var qualifiedName = ToolNaming.BuildMcpQualifiedName(server.Name, tool.Name);
                registrations.Add((new ToolOutputLimitingFunction(new ConnectionFunction(
                    tool.WithName(qualifiedName), this, server.Id, client, calls, metadata), _outputStore), metadata));
            }

            var qualifiedTools = registrations.ConvertAll(registration => registration.Tool);
            (AIFunction Tool, ExternalToolMetadata Metadata) duplicate;
            lock (_clientLock)
            {
                // Reserve each name before publication so collisions within this batch are rejected too.
                var toolNames = _tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
                duplicate = registrations.FirstOrDefault(registration => !toolNames.Add(registration.Tool.Name));

                if (duplicate.Tool is null)
                {
                    foreach (var registration in registrations)
                    {
                        _toolMetadata[registration.Tool.Name] = registration.Metadata;
                    }

                    _serverTools[server.Id] = qualifiedTools;
                    _tools.AddRange(qualifiedTools);
                }
            }

            if (duplicate.Tool is not null)
            {
                _logger.LogWarning(
                    "MCP server '{McpServerId}' exposes duplicate tool name '{ToolName}'.",
                    server.Id,
                    duplicate.Metadata.OriginalName);
                await RemoveServerAsync(server.Id, cancellationToken).ConfigureAwait(false);
                return new McpServerConnectionResult
                {
                    ServerId = server.Id,
                    ServerName = server.Name,
                    Error = $"Tool name '{duplicate.Tool.Name}' is already exposed by an MCP server."
                };
            }

            _ = ObserveCompletionAsync(server.Id, server.Name, client);
            _logger.LogDebug("Connected to MCP server '{Name}'. Tools discovered: {Count}", server.Name, qualifiedTools.Count);
            return new McpServerConnectionResult
            {
                ServerId = server.Id,
                ServerName = server.Name,
                IsConnected = true,
                ToolCount = qualifiedTools.Count
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RemoveServerAsync(server.Id, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to MCP server '{McpServerId}'.", server.Id);
            await RemoveServerAsync(server.Id, cancellationToken).ConfigureAwait(false);
            return new McpServerConnectionResult
            {
                ServerId = server.Id,
                ServerName = server.Name,
                Error = ex.Message
            };
        }
    }

    // One AllowedDirectory per user workspace, read-write, at /workspace/<name> (same rule as the shell sandbox).
    private IReadOnlyList<AllowedDirectory> BuildWorkspaceMounts()
    {
        return _userDirectoryService.ListWorkspaces(_userId)
            .Select(name => new AllowedDirectory(
                _userDirectoryService.GetWorkspaceDirectoryPath(_userId, name),
                name,
                DirectorySourceKind.LocalPath,
                WorkspaceAccessMode.ReadWrite,
                AgentPath: PathHelper.ToWorkspaceAgentPath(name)))
            .ToList();
    }

    private bool IsServerTracked(Guid serverId)
    {
        lock (_clientLock)
        {
            return _clients.ContainsKey(serverId) || _serverTools.ContainsKey(serverId);
        }
    }

    private McpClient? DetachServer(Guid serverId, McpClient? expectedClient = null)
    {
        lock (_clientLock)
        {
            if (expectedClient is not null &&
                (!_clients.TryGetValue(serverId, out var currentClient) ||
                 !ReferenceEquals(currentClient, expectedClient)))
            {
                return null;
            }

            _clients.Remove(serverId, out var client);
            _calls.Remove(serverId);

            if (_serverTools.Remove(serverId, out var tools))
            {
                foreach (var tool in tools)
                {
                    _tools.Remove(tool);
                    _toolMetadata.Remove(tool.Name);
                }
            }

            return client;
        }
    }

    // Warnings mirror into the owner's inbox only; without the scope they reach every operator's system inbox.
    private IDisposable? BeginUserScope() =>
        _logger.BeginScope(new Dictionary<string, object?> { ["userId"] = _userId });

    // ServerInfo throws when a server negotiated through discovery omits its optional identity metadata.
    private static string GetServerInfoName(McpClient client)
    {
        try
        {
            return client.ServerInfo.Name;
        }
        catch (InvalidOperationException)
        {
            return "(unknown)";
        }
    }

    private async Task ObserveCompletionAsync(Guid serverId, string serverName, McpClient client)
    {
        // Completion resolves with the closure details instead of throwing, so the transport failure is read off the result.
        var completion = await client.Completion.ConfigureAwait(false);
        using var userScope = BeginUserScope();

        var detachedClient = DetachServer(serverId, client);
        if (detachedClient is null)
        {
            return;
        }

        // A crashed stdio server explains itself on stderr; surface that rather than a bare disconnect.
        if (completion is StdioClientCompletionDetails stdio)
        {
            _logger.LogWarning(
                completion.Exception,
                "MCP server '{McpServerId}' disconnected; its tools were removed. Exit code: {ExitCode}. Error output: {StandardError}",
                serverId,
                stdio.ExitCode,
                stdio.StandardErrorTail is null ? "(none)" : string.Join(Environment.NewLine, stdio.StandardErrorTail));
        }
        else
        {
            _logger.LogWarning(
                completion.Exception,
                "MCP server '{McpServerId}' disconnected; its tools were removed.",
                serverId);
        }

        try
        {
            ToolCollectionChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh tools after MCP server '{McpServerId}' disconnected.", serverId);
        }

        try
        {
            await detachedClient.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while disposing disconnected MCP client for server {ServerId}.", serverId);
        }
    }

    private async Task DisposeAllClientsAsync()
    {
        McpClient[] clients;
        lock (_clientLock)
        {
            clients = _clients.Values.ToArray();
            _clients.Clear();
            _calls.Clear();
            _serverTools.Clear();
            _tools.Clear();
            _toolMetadata.Clear();
        }

        await Task.WhenAll(clients.Select(client => client.DisposeAsync().AsTask())).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_reaper is not null)
        {
            _executions!.ConfigurationChanged -= ConfigurationChangedAsync;
            _executions.Changed -= PermissionsChangedAsync;
            await _stopping.CancelAsync().ConfigureAwait(false);
            await _reaper.ConfigureAwait(false);
        }

        await Task.WhenAll(_projectHubs.Values.Select(hub => hub.DisposeAllClientsAsync())
            .Append(DisposeAllClientsAsync())).ConfigureAwait(false);
        _stopping.Dispose();
    }

    private sealed class ToolOutputLimitingFunction(AIFunction inner, IToolOutputStore store) : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            object? result = await InnerFunction.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
            if (result is TextContent text)
            {
                return new TextContent(ToolOutput.LimitText(store, "mcp", text.Text));
            }

            if (result is AIContent[] contents && contents.All(static content => content is TextContent))
            {
                string joined = string.Join("\n", contents.Cast<TextContent>().Select(static content => content.Text));
                return new TextContent(ToolOutput.LimitText(store, "mcp", joined));
            }

            if (result is JsonElement json && json.GetRawText().Length > ToolOutput.InlineTextChars && HasTextOnlyContent(json))
            {
                return ToolOutput.LimitText(store, "mcp", json.GetRawText());
            }

            return result;
        }

        private static bool HasTextOnlyContent(JsonElement json)
        {
            if (json.ValueKind != JsonValueKind.Object ||
                !json.TryGetProperty("content", out JsonElement content) ||
                content.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            return content.EnumerateArray().All(static item =>
                item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("type", out JsonElement type) &&
                type.ValueKind == JsonValueKind.String &&
                type.GetString() == "text");
        }
    }

    /// <summary>Serializes one connection's requests so input belongs to its admitted caller.</summary>
    private sealed class ConnectionCalls
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public ActiveCall? Current;
        public DateTimeOffset LastUsed = DateTimeOffset.UtcNow;
        public string? ResourceKey;
        public string? ContainerKey;
        public long ContainerVersion;
        public long Version;
        public string Destination = "Server";
    }

    /// <summary>Retains the exact input route until this call finishes or is cancelled.</summary>
    private sealed record ActiveCall(ElicitationContext? Context, CancellationToken Token);

    /// <summary>Binds MCP callbacks to the caller rather than the user's other active chats.</summary>
    private sealed class ConnectionFunction(AIFunction inner, McpServerHub owner, Guid serverId,
        McpClient client, ConnectionCalls calls, ExternalToolMetadata metadata) : DelegatingAIFunction(inner)
    {
        public override IReadOnlyDictionary<string, object?> AdditionalProperties { get; } =
            new Dictionary<string, object?>(inner.AdditionalProperties) { [ToolMetadataProperty] = metadata };

        public override string Description => $"{InnerFunction.Description}\nExecution environment: {calls.Destination}.";

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var run = owner._runContextAccessor.Current;
            var definition = owner._settings?.GetAvailable().FirstOrDefault(server => server.Id == serverId);
            if (definition?.NodeId is not null && owner._project?.NodeId != definition.NodeId)
            {
                throw new InvalidOperationException("Select a project on this MCP connection's device before using its tools.");
            }

            if (run is not null && !string.Equals(run.UserId, owner._userId, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("The MCP connection belongs to another user.");
            }

            if (run?.ProjectId != owner._project?.Id && owner._settings is not null)
            {
                throw new InvalidOperationException("This MCP tool belongs to another project. Start a new run.");
            }

            using var permission = owner._executions?.Register(owner._userId,
                owner._project is null ? [] : [UserPermissionKeys.Projects], cancellationToken);
            using var configuration = calls.ResourceKey is null ? null : owner._executions!.RegisterConfiguration(
                calls.ResourceKey, calls.Version, owner._userId, cancellationToken);
            using var container = calls.ContainerKey is null || owner._executions is null ? null : owner._executions.RegisterConfiguration(
                calls.ContainerKey, calls.ContainerVersion, owner._userId, cancellationToken);
            using var admitted = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                permission?.Token ?? cancellationToken, configuration?.Token ?? cancellationToken, container?.Token ?? cancellationToken);
            cancellationToken = admitted.Token;
            if (owner._project is not null) { await owner._projects!.AuthorizeAsync(owner._project.Id, cancellationToken).ConfigureAwait(false); }

            if (calls.ResourceKey is { } resourceKey) { run?.ConfigurationScope?.Track(owner._executions!, resourceKey, calls.Version); }

            if (calls.ContainerKey is { } containerKey && owner._executions is not null)
            { run?.ConfigurationScope?.Track(owner._executions, containerKey, calls.ContainerVersion); }

            await calls.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                if (client.Completion.IsCompleted)
                {
                    throw new InvalidOperationException("The MCP connection closed. Start a new request to reconnect.");
                }

                lifetime.Token.ThrowIfCancellationRequested();
                Volatile.Write(ref calls.Current, new ActiveCall(run?.Elicitation, lifetime.Token));
                var result = await InnerFunction.InvokeAsync(arguments, lifetime.Token).ConfigureAwait(false);
                lifetime.Token.ThrowIfCancellationRequested();
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Closing before releasing the gate prevents cancelled work from questioning the next caller.
                var detached = owner.DetachServer(serverId, client);
                if (detached is not null) { await detached.DisposeAsync().ConfigureAwait(false); }

                throw;
            }
            finally
            {
                calls.LastUsed = DateTimeOffset.UtcNow;
                Volatile.Write(ref calls.Current, null);
                try { await lifetime.CancelAsync().ConfigureAwait(false); }
                finally { calls.Gate.Release(); }
            }
        }
    }

}
