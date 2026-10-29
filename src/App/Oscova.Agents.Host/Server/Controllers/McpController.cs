using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oscova.Agents.Core.ApiContracts.Mcp;
using Oscova.Agents.Core.Mcp;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Runtime;

namespace Oscova.Agents.Host.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/mcp")]
[IgnoreAntiforgeryToken]
[GovernedConfigurationException]
public sealed class McpController(UserServiceManager userServiceManager, ILogger<McpController> logger,
    Oscova.Agents.Core.Auth.ExecutionPermissionRegistry executions,
    Oscova.Agents.Host.Server.Nodes.NodeBroker nodes)
    : UserScopeControllerBase(userServiceManager)

{
    [HttpGet]
    public async Task<ActionResult<McpConfiguration>> Get(CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);

        return Ok(new McpConfiguration
        {
            Servers = userScope.McpToolSettings.GetVisible(),
            CanManage = await userScope.McpToolSettings.CanManageAsync(ct),
            IsOperator = !userScope.McpToolSettings.UsesOperatorCatalog,
            ManagedServerIds = userScope.McpToolSettings.GetAvailable()
                .Where(server => !userScope.McpToolSettings.McpServerConfigs.Any(owned => owned.Id == server.Id))
                .Select(server => server.Id).ToArray(),

            ConnectedTools = [.. userScope.McpServerHub.Tools
                .Select(tool =>
                {
                    var metadata = userScope.McpServerHub.ToolMetadata.TryGetValue(tool.Name, out var value)
                        ? value
                        : new ExternalToolMetadata(string.Empty, tool.Name);

                    return new McpTool
                    {
                        QualifiedName = tool.Name,
                        ServerName = metadata.ServerName,
                        OriginalName = metadata.OriginalName,
                        Description = tool.Description
                    };
                })
                .OrderBy(tool => tool.ServerName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(tool => tool.OriginalName, StringComparer.OrdinalIgnoreCase)]
        });
    }

    [HttpPut]
    public async Task<IActionResult> Save([FromBody] SaveMcpConfigurationRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return BadRequest("MCP configuration request is required.");
        }

        var (servers, normalizationError) = NormalizeServerConfigs(request.Servers);
        if (normalizationError is not null)
        {
            return BadRequest(normalizationError);
        }

        var normalizedServers = servers ?? [];
        var validationError = ValidateServerConfigs(normalizedServers);
        if (validationError is not null)
        {
            return BadRequest(validationError);
        }

        var userScope = await GetUserScopeAsync(ct);
        await userScope.McpToolSettings.SaveDefinitionsAsync(normalizedServers, request.ConfirmInterruption, ct);
        SyncMcpTools(userScope);

        return NoContent();
    }

    [HttpPost("servers")]
    public async Task<ActionResult<McpServerSaveResult>> AddServer([FromBody] McpServerConfig request, CancellationToken ct)
    {
        if (request is null)
        {
            return BadRequest("MCP server request is required.");
        }

        var (server, normalizationError) = NormalizeServerConfig(request);
        if (normalizationError is not null)
        {
            return BadRequest(normalizationError);
        }

        var normalizedServer = server;
        if (normalizedServer is null)
        {
            return BadRequest("MCP server request is required.");
        }

        var validationError = ValidateServerConfig(normalizedServer);
        if (validationError is not null)
        {
            return BadRequest(validationError);
        }

        var userScope = await GetUserScopeAsync(ct);
        if (normalizedServer.Id == Guid.Empty)
        {
            normalizedServer.Id = Guid.NewGuid();
        }

        await userScope.McpToolSettings.SaveDefinitionsAsync(
            [.. userScope.McpToolSettings.McpServerConfigs, normalizedServer], false, ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
        var connection = await ConnectAsync(userScope, normalizedServer, timeoutCts.Token);
        SyncMcpTools(userScope);

        return Ok(new McpServerSaveResult
        {
            Server = normalizedServer,
            Connection = connection
        });
    }

    [HttpDelete("servers/{serverId:guid}")]
    public async Task<IActionResult> DeleteServer(Guid serverId, CancellationToken ct,
        [FromQuery] bool confirmInterruption = false)

    {
        var userScope = await GetUserScopeAsync(ct);
        var server = userScope.McpToolSettings.McpServerConfigs.FirstOrDefault(existing => existing.Id == serverId);
        if (server is null)
        {
            return NotFound();
        }

        await userScope.McpToolSettings.SaveDefinitionsAsync(
            userScope.McpToolSettings.McpServerConfigs.Where(existing => existing.Id != serverId).ToArray(),
            confirmInterruption, ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
        await userScope.McpServerHub.RemoveServerAsync(serverId, timeoutCts.Token);
        SyncMcpTools(userScope);

        return NoContent();
    }

    [HttpPost("servers/{serverId:guid}/restart")]
    public async Task<ActionResult<McpServerReconnectResult>> RestartServer(Guid serverId, CancellationToken ct,
        [FromQuery] bool confirmed = false)
    {
        var userScope = await GetUserScopeAsync(ct);
        var server = userScope.McpToolSettings.McpServerConfigs.FirstOrDefault(existing => existing.Id == serverId);
        if (server is null)
        {
            return NotFound();
        }

        await RestartDefinitionsAsync(userScope, [server], confirmed, ct);
        var connection = await ConnectAsync(userScope, server, ct);
        SyncMcpTools(userScope);

        return Ok(new McpServerReconnectResult
        {
            Connections = [connection]
        });
    }

    [HttpPost("reconnect-all")]
    public async Task<ActionResult<McpServerReconnectResult>> ReconnectAll(CancellationToken ct,
        [FromQuery] bool confirmed = false)
    {
        var userScope = await GetUserScopeAsync(ct);
        if (await userScope.McpToolSettings.CanManageAsync(ct))
        {
            await RestartDefinitionsAsync(userScope, userScope.McpToolSettings.McpServerConfigs.ToArray(), confirmed, ct);
        }

        var connections = new List<McpServerConnectionResult>();
        try
        {
            foreach (var server in userScope.McpToolSettings.GetAvailable().Where(server => server.IsEnabled))
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    connections.Add(await ConnectAsync(userScope, server, timeoutCts.Token));
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    connections.Add(new McpServerConnectionResult
                    {
                        ServerId = server.Id,
                        ServerName = server.Name,
                        Error = "Connection timed out."
                    });
                }
            }
        }
        finally
        {
            SyncMcpTools(userScope);
        }

        return Ok(new McpServerReconnectResult
        {
            Connections = connections
        });
    }

    private async Task RestartDefinitionsAsync(UserServiceScope scope, IReadOnlyList<McpServerConfig> servers,
        bool confirmed, CancellationToken ct)
    {
        if (!await scope.McpToolSettings.CanManageAsync(ct))
        {
            throw new UnauthorizedAccessException("MCP management permission is required to restart connections.");
        }

        await executions.ChangeConfigurationAsync(() => servers.Select(server => scope.McpToolSettings.GetResourceKey(server.Id)).ToArray(),
            () => Task.CompletedTask, confirmed, ct);
    }

    private async Task<McpServerConnectionResult> ConnectAsync(UserServiceScope scope, McpServerConfig server, CancellationToken ct)
    {
        if (!server.IsEnabled) { return new() { ServerId = server.Id, ServerName = server.Name }; }

        if (server.NodeId is { } nodeId && nodes.GetConnected(scope.UserId, nodeId) is null)
        {
            return new() { ServerId = server.Id, ServerName = server.Name, DeviceOffline = true };
        }

        return await scope.McpServerHub.AddServerAsync(server, ct);
    }

    private static (List<McpServerConfig>? Servers, string? Error) NormalizeServerConfigs(IEnumerable<McpServerConfig>? servers)
    {
        if (servers is null)
        {
            return (null, "MCP server list is required.");
        }

        var normalizedServers = new List<McpServerConfig>();
        foreach (var server in servers)
        {
            var (normalizedServer, error) = NormalizeServerConfig(server);
            if (error is not null)
            {
                return (null, error);
            }

            if (normalizedServer is null)
            {
                return (null, "MCP server entries are required.");
            }

            normalizedServers.Add(normalizedServer);
        }

        return (normalizedServers, null);
    }

    private static (McpServerConfig? Server, string? Error) NormalizeServerConfig(McpServerConfig? server)
    {
        if (server is null)
        {
            return (null, "MCP server entries are required.");
        }

        var normalized = server.Clone();
        if (string.IsNullOrWhiteSpace(normalized.NodeId) || normalized.IsShared) { normalized.NodeId = null; }

        if (normalized.Transport == McpTransportType.Http)
        {
            normalized.NodeId = null;
            normalized.Command = null;
            normalized.ExecutionEnvironment = McpExecutionEnvironment.HostLocal;
            normalized.Arguments = null;
            normalized.WorkingDirectory = null;
            normalized.EnvironmentVariables = null;
        }
        else
        {
            normalized.Endpoint = null;
            normalized.Headers = null;
        }

        return (normalized, null);
    }

    private static string? ValidateServerConfigs(IEnumerable<McpServerConfig>? servers)
    {
        if (servers is null)
        {
            return "MCP server list is required.";
        }

        foreach (var server in servers)
        {
            if (server is null)
            {
                return "MCP server entries are required.";
            }

            var validationError = ValidateServerConfig(server);
            if (validationError is not null)
            {
                return validationError;
            }
        }

        return null;
    }

    private static string? ValidateServerConfig(McpServerConfig server)
    {
        if (!Enum.IsDefined(server.NetworkScope))
        {
            return "MCP server network scope is invalid.";
        }

        // Disabled registry definitions may await credentials while other connections are edited.
        if (server.Headers?.Any(header =>
                string.IsNullOrWhiteSpace(header.Key) ||
                server.IsEnabled && string.IsNullOrWhiteSpace(header.Value)) == true)
        {
            return "MCP server headers must include both a name and a value.";
        }

        if (server.Arguments?.Any(string.IsNullOrWhiteSpace) == true)
        {
            return "MCP server arguments must not be blank.";
        }

        if (server.EnvironmentVariables?.Any(variable =>
                string.IsNullOrWhiteSpace(variable.Key) ||
                server.IsEnabled && variable.Value is not null && string.IsNullOrWhiteSpace(variable.Value)) == true)
        {
            return "MCP server environment variables must include a name, and literal values must not be blank.";
        }

        return null;
    }

    private void SyncMcpTools(UserServiceScope userScope)
    {
        var mcpToolCount = userScope.McpServerHub.Tools.Count;

        userScope.AppService.SyncTools();
        var totalToolCountAfterSync = userScope.AppService.AllTools.Count;
        userScope.AppService.InvalidateAllCachedAgents();
        userScope.AppService.NotifyStateChanged();

        logger.LogDebug(
            "Synchronized MCP tools after configuration change. MCP tools: {McpToolCount}, total tools after sync: {TotalToolCountAfterSync}, total tools after invalidating agents: {TotalToolCountAfterInvalidation}",
            mcpToolCount,
            totalToolCountAfterSync,
            userScope.AppService.AllTools.Count);
    }

}
