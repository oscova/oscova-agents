using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Conversations;
using Oscova.Agents.Core.ApiContracts.Mcp.Registry;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Mcp;
using Oscova.Agents.Core.Mcp.Registry;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Definitions;
using Oscova.Agents.Harness;
using Oscova.Agents.Infrastructure.Utils;
using Oscova.Agents.Policies;
using Oscova.Agents.Runtime;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Oscova.Agents.Infrastructure.Toolsets;

/// <summary>
/// Lets the active agent search the official MCP registry and install a server when setup can be completed safely.
/// </summary>
public sealed class McpRegistryToolset(
    string userId,
    IMcpRegistryService registry,
    McpToolSettings mcpToolSettings,
    IMcpServerHub mcpServerHub,
    IAgentDefinitionRepository agentDefinitions,
    IAgentCacheInvalidator cacheInvalidator,
    RunContextAccessor runContext,
    ILogger<McpRegistryToolset> logger) : IAgentToolset
{
    private const int DefaultMaxResults = 5;
    private const int MaxResultsCeiling = 10;
    private static readonly string InstallToolName =
        ToolNaming.BuildToolsetQualifiedName<McpRegistryToolset>(nameof(InstallMcpServerAsync));
    private readonly ConcurrentDictionary<string, McpRegistryServer> _reviewed = new(StringComparer.Ordinal);

    /// <summary>
    /// Refreshes the crew tool collection after a live MCP connect so newly discovered tools can be granted immediately.
    /// </summary>
    public Action? RefreshToolCollection { get; set; }

    /// <summary>Discovers newly installed tools in the current project's retained connection.</summary>
    public Func<RunContext, CancellationToken, Task>? ConnectProjectToolsAsync { get; set; }

    [DisplayName("search")]
    [AgentTool]
    [Description("Search the official MCP registry using part of a server name.\nIf the requested server is missing, refine query or increase max.\nReturns up to max matches with install options and required fields. total counts retrieved matches. hasMore marks omitted matches.\nUse mcpregistry__install with the server 'name' and chosen option 'kind'.")]
    [ToolNetworkScope(NetworkScope.Egress)]
    public async Task<string> SearchMcpServersAsync(
        [Description("A nonblank part of a server name.")] string query,
        [Description("Maximum results to return, from 1 to 10.")] int max = DefaultMaxResults,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw Failed("An MCP registry search query is required.");
        }

        var cappedMax = Math.Clamp(max, 1, MaxResultsCeiling);
        IReadOnlyList<McpRegistryServer> servers;
        try
        {
            servers = await registry.SearchAsync(query, cancellationToken).ConfigureAwait(false);
        }
        catch (McpRegistryException ex)
        {
            logger.LogError(ex, "Agent MCP registry search failed for query '{Query}'.", query);
            throw Failed("The MCP registry could not be reached.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent MCP registry search failed unexpectedly for query '{Query}'.", query);
            throw;
        }

        var results = servers
            .Take(cappedMax)
            .Select(McpRegistryServerMapper.Map)
            .Select(entry => new
            {
                name = entry.Name,
                title = entry.Title,
                description = entry.Description,
                version = entry.Version,
                options = entry.Options.Select(option => new
                {
                    kind = option.Kind,
                    label = option.Label,
                    isManual = option.IsManual,
                    requiredFields = option.RequiredFields
                })
            })
            .ToList();

        // Count before limiting so the caller can recognize an incomplete result.
        return JsonSerializer.Serialize(new { query, total = servers.Count, hasMore = servers.Count > results.Count, results });
    }

    /// <summary>Uses the account's existing project authorization when choosing an install destination.</summary>
    public Func<string, CancellationToken, Task<Oscova.Agents.Core.Settings.ProjectDefinition>> ResolveProjectAsync { get; set; } =
        (_, _) => throw new InvalidOperationException("Project authorization is unavailable.");

    [DisplayName("install")]
    [AgentTool]
    [Description("""
        Install an MCP server from the official registry and assign its tools to this agent.
        Start a fresh chat to use the assigned tools.
        Pass the server 'name' from search, the chosen option 'kind', and its network scope.
        Missing secrets or runtimes leave setup pending for the user to finish in Settings.
        Requires user approval.
        """)]
    [ToolNetworkScope(NetworkScope.Egress)]
    [ToolPolicy(ApprovalMode = ToolApprovalMode.RequireApproval)]
    public async Task<string> InstallMcpServerAsync(
        [Description("The canonical server name from mcpregistry__search, for example 'io.github.owner/server'.")] string name,
        [Description("The install option 'kind' to use, copied from the chosen option in search results.")] string option,
        [Description("The server's network use: None, Incidental, or Egress. This declaration is shown for user approval and controls air-gap filtering.")] NetworkScope networkScope,
        CancellationToken cancellationToken = default)
    {
        var context = runContext.Current;
        if (context is null || string.IsNullOrWhiteSpace(context.CurrentAgentId))
        {
            throw Failed("No active agent context; cannot install an MCP server.");
        }

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(option))
        {
            throw Failed("Both an MCP server name and install option are required.");
        }

        if (!Enum.IsDefined(networkScope))
        {
            throw Failed("The MCP server network scope is invalid.");
        }

        McpRegistryServer? server = null;
        try
        {
            // Install the release the user reviewed, even if the registry changed while approval was pending.
            if (FunctionInvokingChatClient.CurrentContext?.CallContent.CallId is not { } callId ||
                !_reviewed.TryRemove(callId, out server) || server.Name != name)
            {
                server = await FindServerAsync(name, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (McpRegistryException ex)
        {
            logger.LogError(ex, "Agent MCP install could not resolve '{Name}'.", name);
            throw Failed("The MCP registry could not be reached.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent MCP install could not resolve '{Name}' unexpectedly.", name);
            throw;
        }

        if (server is null)
        {
            throw Failed("No MCP registry server with that exact name was found.");
        }

        var entry = McpRegistryServerMapper.Map(server);
        var chosen = entry.Options.FirstOrDefault(candidate => string.Equals(candidate.Kind, option, StringComparison.OrdinalIgnoreCase));
        if (chosen is null)
        {
            throw new ToolFailureException(
                "The selected MCP install option is unavailable.",
                "Use an option returned by mcpregistry__search.");
        }

        if (chosen.IsManual || chosen.Server is null)
        {
            return SavedPending(
                name,
                chosen,
                missingRuntime: null,
                "This server only offers manual setup. Open Settings to configure it.");
        }

        var config = chosen.Server.Clone();
        config.Id = CreateRegistryServerId(entry.Name, chosen.Kind);
        config.NetworkScope = networkScope;
        if (context.ProjectId is not null && config.Transport == McpTransportType.Stdio)
        {
            var project = await ResolveProjectAsync(context.ProjectId, cancellationToken).ConfigureAwait(false);
            if (project.NodeId is { } nodeId)
            {
                config.NodeId = nodeId;
            }
        }
        // Registry installs are singletons per server option; block repeats so user-edited settings stay intact.
        if (mcpToolSettings.McpServerConfigs.Any(existing => existing.Id == config.Id))
        {
            return JsonSerializer.Serialize(new
            {
                status = "already_installed",
                name,
                serverName = config.Name,
                message = "This MCP server is already installed. Open Settings to edit or reconnect it."
            });
        }

        // Saved off when values are missing so chat requests do not retry a server that cannot connect yet.
        // The Settings editor turns it on once the user fills the blank values.
        if (chosen.RequiredFields.Count > 0)
        {
            config.IsEnabled = false;
        }

        if (config.NodeId is null && config.Transport == McpTransportType.Stdio &&
            config.ExecutionEnvironment == McpExecutionEnvironment.HostLocal &&
            !string.IsNullOrWhiteSpace(config.Command) &&
            ExecutableLocator.FindOnPath(config.Command) is null)
        {
            await PersistAsync(config).ConfigureAwait(false);
            return SavedPending(
                name,
                chosen,
                config.Command,
                $"The '{config.Command}' runtime is not installed on this machine. Install it, then reconnect this server in Settings.");
        }

        if (chosen.RequiredFields.Count > 0)
        {
            await PersistAsync(config).ConfigureAwait(false);
            return SavedPending(
                name,
                chosen,
                missingRuntime: null,
                "This server needs values you must enter in Settings, such as an API key. The server was saved; open Settings to fill the required fields and reconnect.");
        }

        await PersistAsync(config).ConfigureAwait(false);
        McpServerConnectionResult connection;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
            if (context.ProjectId is not null)
            {
                var connect = ConnectProjectToolsAsync ?? throw new InvalidOperationException("Project MCP execution is not initialized.");
                await connect(context, timeoutCts.Token).ConfigureAwait(false);
                var count = mcpServerHub.ToolMetadata.Values.Count(tool => tool.ServerName == config.Name);
                connection = new McpServerConnectionResult
                {
                    ServerId = config.Id, ServerName = config.Name, IsConnected = count > 0, ToolCount = count
                };
            }
            else { connection = await mcpServerHub.AddServerAsync(config, timeoutCts.Token).ConfigureAwait(false); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent MCP install saved server {McpServerId}, but connection failed.", config.Id);
            throw new ToolFailureException(
                "The MCP server was saved but could not connect.",
                "Open Settings and reconnect it.");
        }

        if (!connection.IsConnected)
        {
            logger.LogError(
                "Agent MCP install saved server {McpServerId}, but connection failed: {Error}",
                config.Id,
                connection.Error);
            throw new ToolFailureException(
                "The MCP server was saved but could not connect.",
                "Open Settings and reconnect it.");
        }

        IReadOnlyList<string> grantedTools;
        try
        {
            RefreshToolCollection?.Invoke();
            grantedTools = await GrantConnectedToolsAsync(config.Name, context.CurrentAgentId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent MCP install failed unexpectedly after connecting server {McpServerId}.", config.Id);
            throw;
        }

        if (grantedTools.Count == 0)
        {
            throw new ToolFailureException(
                "The MCP server was installed and connected, but its tools could not be assigned.",
                "Open Settings and grant its tools to the agent.");
        }

        return JsonSerializer.Serialize(new
        {
            status = "connected",
            name,
            serverName = config.Name,
            toolCount = connection.ToolCount,
            grantedTools,
            message = "Installed and connected. The server's tools are assigned to this agent. Start a fresh chat to use them."
        });
    }

    /// <summary>Describes a pending install from registry data so the user reviews what will actually run.</summary>
    public async Task<ToolApprovalReview?> DescribeInstallAsync(FunctionCallContent call, CancellationToken cancellationToken)
    {
        if (call.Name != InstallToolName ||
            ToolApprovalRequestBuilder.TryGetString(call.Arguments, "name") is not { } name)
        {
            return null;
        }

        McpRegistryServer? server;
        try
        {
            server = await FindServerAsync(name, cancellationToken).ConfigureAwait(false);
        }
        catch (McpRegistryException ex)
        {
            // The generic card still asks for approval, and the install itself reports the registry failure.
            logger.LogWarning(ex, "MCP install review could not resolve '{Name}'.", name);
            return null;
        }

        if (server is null)
        {
            return null;
        }

        _reviewed[call.CallId] = server;
        var option = ToolApprovalRequestBuilder.TryGetString(call.Arguments, "option");
        var config = McpRegistryServerMapper.Map(server).Options
            .FirstOrDefault(candidate => string.Equals(candidate.Kind, option, StringComparison.OrdinalIgnoreCase))?.Server;
        var scope = ToolApprovalRequestBuilder.TryGetString(call.Arguments, "networkScope");
        var slash = server.Name.IndexOf('/', StringComparison.Ordinal);
        return new ToolApprovalReview
        {
            Kind = "registry-install",
            Operation = "mcp-server",
            ServerName = server.Name,
            Publisher = slash > 0 ? server.Name[..slash] : null,
            Version = server.Version,
            SourceRepository = server.Repository?.Url,
            Transport = config?.Transport.ToString(),
            Command = config?.Transport == McpTransportType.Stdio
                ? string.Join(' ', [config.Command, .. config.Arguments ?? []])
                : config?.Endpoint,
            NetworkScope = Enum.TryParse<NetworkScope>(scope, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
                ? parsed.ToString()
                : null
        };
    }

    private async Task<McpRegistryServer?> FindServerAsync(string name, CancellationToken cancellationToken)
    {
        var matches = await registry.SearchAsync(name, cancellationToken).ConfigureAwait(false);
        return matches.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
    }

    private async Task PersistAsync(McpServerConfig config)
    {
        await mcpToolSettings.SaveDefinitionsAsync(
            [.. mcpToolSettings.McpServerConfigs.Where(existing => existing.Id != config.Id), config],
            false, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> GrantConnectedToolsAsync(
        string serverName,
        string agentId,
        CancellationToken cancellationToken)
    {
        var qualifiedNames = mcpServerHub.ToolMetadata
            .Where(pair => string.Equals(pair.Value.ServerName, serverName, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key)
            .ToList();

        var agent = await agentDefinitions.GetAgentAsync(userId, agentId, cancellationToken).ConfigureAwait(false);
        if (agent is null || qualifiedNames.Count == 0)
        {
            return [];
        }

        var added = false;
        foreach (var toolName in qualifiedNames)
        {
            if (!agent.AllowedToolNames.Contains(toolName, StringComparer.OrdinalIgnoreCase))
            {
                agent.AllowedToolNames.Add(toolName);
                added = true;
            }
        }

        if (added)
        {
            try
            {
                await agentDefinitions.SaveAgentAsync(userId, agent, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or IOException)
            {
                logger.LogError(ex, "Agent MCP install connected but could not assign tools to agent {AgentId}.", agentId);
                return [];
            }

            cacheInvalidator.InvalidateAllAgents();
        }

        return qualifiedNames;
    }

    /// <summary>
    /// Creates a deterministic id for registry installs so repeat requests can find the original saved config.
    /// </summary>
    private static Guid CreateRegistryServerId(string registryName, string optionKind)
    {
        var stableKey = JsonSerializer.Serialize(new[]
        {
            "oscova-mcp-registry-install",
            registryName.Trim(),
            optionKind.Trim().ToLowerInvariant()
        });
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(stableKey));
        return new Guid(hash[..16]);
    }

    private static string SavedPending(
        string name,
        McpRegistryInstallOption option,
        string? missingRuntime,
        string message) =>
        JsonSerializer.Serialize(new
        {
            status = "saved_pending",
            name,
            option = option.Kind,
            missingRuntime,
            requiredFields = option.RequiredFields,
            message
        });

    private static ToolFailureException Failed(string reason) => new(reason);
}
