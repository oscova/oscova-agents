using Oscova.Agents.Core.Auth;
using Oscova.Agents.Core.Interfaces;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Oscova.Agents.Settings;
using Oscova.Agents.Core.Mcp;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace Oscova.Agents.Core.Settings;

/// <summary>
/// MCP server configuration settings.
/// </summary>
public class McpToolSettings : SettingsBase
{
    public const string Key = "mcp";

    private ObservableCollection<McpServerConfig> _mcpServers = [];

    public McpToolSettings(IAppSettingsRepository repository, string userId, ILoggerFactory loggerFactory)
        : base(repository, userId, loggerFactory)
    {
        _mcpServers.CollectionChanged += McpServersOnCollectionChanged;
    }

    public override string SettingsKey => Key;

    public ObservableCollection<McpServerConfig> McpServerConfigs
    {
        get => _mcpServers;
        set
        {
            if (ReferenceEquals(_mcpServers, value))
            {
                return;
            }

            _mcpServers.CollectionChanged -= McpServersOnCollectionChanged;
            _mcpServers = value ?? [];
            _mcpServers.CollectionChanged += McpServersOnCollectionChanged;

            OnPropertyChanged();
            OnChanged();
        }
    }

    private void McpServersOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnChanged();
    }

    private McpToolSettings? _operatorCatalog;
    private string? _ownerId;
    private IUserPermissionService? _permissions;
    private ExecutionPermissionRegistry? _executions;
    private Func<string, CancellationToken, Task<bool>>? _isOwnedNode;

    public bool UsesOperatorCatalog => _operatorCatalog is not null;

    /// <summary>Binds inherited definitions without copying the operator's settings or secrets into user persistence.</summary>
    public void BindGovernance(string ownerId, McpToolSettings? operatorCatalog,
        IUserPermissionService permissions, ExecutionPermissionRegistry executions,
        Func<string, CancellationToken, Task<bool>> isOwnedNode)
    {
        _ownerId = ownerId;
        _operatorCatalog = operatorCatalog;
        _permissions = permissions;
        _executions = executions;
        _isOwnedNode = isOwnedNode;
    }

    /// <summary>Returns runtime definitions owned by this account or explicitly shared with it.</summary>
    public IReadOnlyList<McpServerConfig> GetAvailable() =>
        [.. _mcpServers, .. _operatorCatalog?._mcpServers.Where(server => server.IsShared) ?? []];

    /// <summary>Uses the definition owner's identity for interruption across all consuming accounts.</summary>
    public string GetResourceKey(Guid serverId)
    {
        var owner = _mcpServers.Any(server => server.Id == serverId) ? this : _operatorCatalog;
        if (owner?._ownerId is not { } ownerId) { throw new KeyNotFoundException("MCP connection is unavailable."); }

        return $"mcp/{ownerId}/{serverId:N}";
    }

    /// <summary>Lists shared connections without exposing their launch definitions or credential values.</summary>
    public IReadOnlyList<McpServerConfig> GetVisible() => [.. _mcpServers.Select(server => server.Clone()),
        .. _operatorCatalog?._mcpServers.Where(server => server.IsShared).Select(server => new McpServerConfig
        {
            Id = server.Id, Name = server.Name, IsShared = true, IsEnabled = server.IsEnabled,
            Transport = server.Transport, NetworkScope = server.NetworkScope
        }) ?? []];

    /// <summary>Applies all managed MCP edits through one authorization and interruption boundary.</summary>
    public async Task SaveDefinitionsAsync(IReadOnlyList<McpServerConfig> definitions, bool confirmed, CancellationToken ct)
    {
        var permissions = _permissions ?? throw new InvalidOperationException("MCP governance is not initialized.");
        var executions = _executions!;
        var ownerId = _ownerId!;
        var isOperator = await permissions.IsOperatorAsync(ownerId, ct).ConfigureAwait(false);
        if (!isOperator) { await permissions.RequireAsync(ownerId, UserPermissionKeys.ManagePersonalMcp, ct).ConfigureAwait(false); }

        var replacements = definitions.Select(server => server.Clone()).ToArray();
        if (replacements.Select(server => server.Id).Distinct().Count() != replacements.Length ||
            replacements.Select(server => server.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != replacements.Length)
        {
            throw new ArgumentException("MCP connection IDs and names must be unique.");
        }

        foreach (var server in replacements)
        {
            if (string.IsNullOrWhiteSpace(server.NodeId)) { server.NodeId = null; }

            if (server.Transport == McpTransportType.Http || server.IsShared) { server.NodeId = null; }

            if (!isOperator && (server.IsShared || server.Transport == McpTransportType.Stdio && server.NodeId is null))
            {
                throw new UnauthorizedAccessException("Only the operator can share connections or configure programs on the server. Select an owned node.");
            }

            if (!isOperator && _operatorCatalog!._mcpServers.Any(shared => shared.Id == server.Id ||
                string.Equals(shared.Name, server.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException("A personal MCP connection cannot replace an operator connection. Choose another name.");
            }

            if (server.NodeId is { } nodeId && !await _isOwnedNode!(nodeId, ct).ConfigureAwait(false))
            {
                throw new UnauthorizedAccessException("Select a device enrolled in your account.");
            }
        }

        await executions.ChangeConfigurationAsync(() =>
        {
            return _mcpServers.Where(old =>
            {
                var replacement = replacements.FirstOrDefault(candidate => candidate.Id == old.Id);
                return replacement is null || JsonSerializer.Serialize(old) != JsonSerializer.Serialize(replacement);
            }).Select(old => GetResourceKey(old.Id)).ToArray();
        }, async () =>
        {
            // Recheck the management grant after any affected work has finished stopping.
            if (!isOperator) { await permissions.RequireAsync(ownerId, UserPermissionKeys.ManagePersonalMcp, ct).ConfigureAwait(false); }

            var previous = McpServerConfigs;
            try
            {
                McpServerConfigs = new ObservableCollection<McpServerConfig>(replacements);
                await SaveAsync().ConfigureAwait(false);
            }
            catch
            {
                McpServerConfigs = previous;
                throw;
            }
        }, confirmed, ct).ConfigureAwait(false);
    }

    /// <summary>Returns the live management grant used by API metadata and managed writes.</summary>
    public async Task<bool> CanManageAsync(CancellationToken ct)
    {
        if (_permissions is null || _ownerId is null) { throw new InvalidOperationException("MCP governance is not initialized."); }

        return await _permissions.IsOperatorAsync(_ownerId, ct).ConfigureAwait(false) ||
            (await _permissions.GetGrantedAsync(_ownerId, ct).ConfigureAwait(false)).Contains(UserPermissionKeys.ManagePersonalMcp);
    }

    public override string ToYaml()
    {
        return Yaml.Serialize(new Snapshot
        {
            McpServerConfigs = _mcpServers.ToList()
        });
    }

    public override void FromYaml(string yaml)
    {
        var data = Yaml.Deserialize<Snapshot>(yaml);
        if (data is null)
        {
            return;
        }

        McpServerConfigs = data.McpServerConfigs is null
            ? []
            : new ObservableCollection<McpServerConfig>(data.McpServerConfigs);
    }

    private sealed class Snapshot
    {
        public List<McpServerConfig>? McpServerConfigs { get; set; }
    }
}
