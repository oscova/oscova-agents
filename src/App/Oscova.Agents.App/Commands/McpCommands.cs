using System.ComponentModel;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Oscova.Agents.App.Cli;
using Oscova.Agents.Core.ApiContracts.Mcp;
using Oscova.Agents.Core.Mcp;
using Oscova.Agents.Host.Constants;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Oscova.Agents.App.Commands;

/// <summary>
/// Lists configured MCP servers and the tools they currently expose.
/// </summary>
public sealed class McpListCommand(CliHostResolver resolver, HostApiClient apiClient) : HostCommandBase<OscovaCommandSettings>(resolver, apiClient)
{
    protected override async Task<int> RunAsync(HttpClient client, OscovaCommandSettings settings, CancellationToken cancellationToken)
    {
        var configuration = await client.GetFromJsonAsync<McpConfiguration>(ApiRoutes.Mcp.Configuration, CliJson.Options, cancellationToken).ConfigureAwait(false)
            ?? new McpConfiguration();

        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(configuration, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var toolCounts = configuration.ConnectedTools
            .GroupBy(tool => tool.ServerName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        if (configuration.Servers.Count == 0)
        {
            AnsiConsole.Write(CliRender.Card(
                "MCP servers (0)",
                new Text("No MCP servers are configured.", CliTheme.Label),
                CliTheme.AccentDim));
        }
        else
        {
            var table = CliRender.Table("ID", "Name", "Transport", "Target", "Tools");
            foreach (var server in configuration.Servers)
            {
                var target = server.Transport == McpTransportType.Http ? server.Endpoint : server.Command;
                table.AddRow(
                    CliRender.Identifier(server.Id.ToString()),
                    CliRender.Value(server.Name),
                    CliRender.Value(server.Transport.ToString()),
                    CliRender.Value(target ?? string.Empty),
                    CliRender.Value(toolCounts.TryGetValue(server.Name, out var count) ? count.ToString() : "0"));
            }

            CliRender.Separator($"MCP servers ({configuration.Servers.Count})");
            AnsiConsole.Write(table);
        }

        return 0;
    }
}

public sealed class McpAddSettings : OscovaCommandSettings
{
    [CommandOption("--name <NAME>")]
    [Description("Display name for the server.")]
    public string? Name { get; init; }

    [CommandOption("--transport <TRANSPORT>")]
    [Description("Transport to connect with: http or stdio. Required unless --file is used.")]
    public string? Transport { get; init; }

    [CommandOption("--endpoint <URL>")]
    [Description("Server URL. Required for the http transport.")]
    public string? Endpoint { get; init; }

    [CommandOption("--command <EXECUTABLE>")]
    [Description("Executable to launch. Required for the stdio transport.")]
    public string? Command { get; init; }

    [CommandOption("--arg <VALUE>")]
    [Description("Argument passed to the stdio command. Repeat the option for each argument.")]
    public string[] Arguments { get; init; } = [];

    [CommandOption("--file <PATH>")]
    [Description("Path to a JSON server definition, for servers needing headers, environment variables, or a working directory.")]
    public string? File { get; init; }
}

/// <summary>
/// Registers an MCP server and connects to it immediately.
/// </summary>
public sealed class McpAddCommand(CliHostResolver resolver, HostApiClient apiClient) : HostCommandBase<McpAddSettings>(resolver, apiClient)
{
    protected override async Task<int> RunAsync(HttpClient client, McpAddSettings settings, CancellationToken cancellationToken)
    {
        var server = settings.File is null ? BuildFromOptions(settings) : ReadFromFile(settings.File);

        using var response = await CliRender.StatusAsync(
            $"Adding MCP server {server.Name}...", settings,
            () => client.PostAsJsonAsync(
                ApiRoutes.Mcp.Servers,
                server,
                CliJson.Options,
                cancellationToken)).ConfigureAwait(false);
        await CliHttpErrors.EnsureSuccessAsync(response, "Adding MCP server", cancellationToken).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<McpServerSaveResult>(CliJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new CliExitException(1, "The host returned an empty response.");

        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(result, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        if (!settings.Quiet && AnsiConsole.Profile.Capabilities.Interactive && AnsiConsole.Profile.Capabilities.Ansi)
        {
            CliRender.KeyValue("MCP server added",
            [
                ("ID", CliRender.Identifier(result.Server.Id.ToString())),
                ("Name", CliRender.Value(result.Server.Name))
            ]);
            CliRender.KeyValue("Connection",
            [
                ("Server", CliRender.Value(result.Connection.ServerName)),
                ("State", CliRender.State(result.Connection.IsConnected ? "connected" : "not connected", result.Connection.IsConnected)),
                ("Tools", CliRender.Value(result.Connection.ToolCount.ToString())),
                ("Detail", CliRender.Value(result.Connection.Error ?? "-"))
            ]);
        }
        else
        {
            CliRender.Success($"Added MCP server {result.Server.Id}.");
            McpCommandSupport.ReportConnection(result.Connection);
        }

        return 0;
    }

    private static McpServerConfig BuildFromOptions(McpAddSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Name))
        {
            throw new CliExitException(4, "--name is required.");
        }

        var transport = settings.Transport?.ToLowerInvariant() switch
        {
            "http" => McpTransportType.Http,
            "stdio" => McpTransportType.Stdio,
            null or "" => throw new CliExitException(4, "--transport is required. Pass http or stdio."),
            _ => throw new CliExitException(4, $"Unknown transport '{settings.Transport}'. Pass http or stdio.")
        };

        var server = new McpServerConfig
        {
            Id = Guid.Empty,
            Name = settings.Name,
            Transport = transport
        };

        if (transport == McpTransportType.Http)
        {
            server.Endpoint = string.IsNullOrWhiteSpace(settings.Endpoint)
                ? throw new CliExitException(4, "--endpoint is required for the http transport.")
                : settings.Endpoint;
        }
        else
        {
            server.Command = string.IsNullOrWhiteSpace(settings.Command)
                ? throw new CliExitException(4, "--command is required for the stdio transport.")
                : settings.Command;
            server.Arguments = settings.Arguments.Length == 0 ? null : [.. settings.Arguments];
        }

        return server;
    }

    private static McpServerConfig ReadFromFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new CliExitException(4, $"No file at '{path}'.");
        }

        try
        {
            var server = JsonSerializer.Deserialize<McpServerConfig>(File.ReadAllText(path), CliJson.Options)
                ?? throw new CliExitException(4, $"'{path}' does not contain an MCP server definition.");
            server.Id = Guid.Empty;
            return server;
        }
        catch (JsonException exception)
        {
            throw new CliExitException(4, $"'{path}' is not valid JSON: {exception.Message}");
        }
    }
}

public sealed class McpServerIdSettings : McpReconnectSettings
{
    [CommandArgument(0, "<SERVER_ID>")]
    [Description("Server identifier (a GUID). List valid ids with 'oscova mcp list'.")]
    public string ServerId { get; init; } = string.Empty;
}

public sealed class McpRemoveSettings : OscovaCommandSettings
{
    [CommandArgument(0, "<SERVER_ID>")]
    [Description("Server identifier (a GUID). List valid ids with 'oscova mcp list'.")]
    public string ServerId { get; init; } = string.Empty;

    [CommandOption("--yes")]
    [Description("Confirm removal and interruption of work using this connection.")]
    public bool Yes { get; init; }
}

/// <summary>
/// Removes an MCP server and disconnects its tools.
/// </summary>
public sealed class McpRemoveCommand(CliHostResolver resolver, HostApiClient apiClient) : HostCommandBase<McpRemoveSettings>(resolver, apiClient)
{
    protected override async Task<int> RunAsync(HttpClient client, McpRemoveSettings settings, CancellationToken cancellationToken)
    {
        var serverId = McpCommandSupport.ParseServerId(settings.ServerId);
        CliRender.ConfirmDestructive(settings, settings.Yes, $"MCP server '{serverId}' will be removed and work using it will stop.");

        using var response = await CliRender.StatusAsync(
            $"Removing MCP server {serverId}...", settings,
            () => client.DeleteAsync(ApiRoutes.Mcp.ServerById(serverId) + "?confirmInterruption=true", cancellationToken)).ConfigureAwait(false);
        McpCommandSupport.ThrowIfMissingServer(response, serverId);
        await CliHttpErrors.EnsureSuccessAsync(response, "Removing MCP server", cancellationToken).ConfigureAwait(false);
        var message = $"Removed MCP server {serverId}.";
        if (!settings.Json && !settings.Quiet &&
            AnsiConsole.Profile.Capabilities.Interactive && AnsiConsole.Profile.Capabilities.Ansi)
        {
            AnsiConsole.Write(CliRender.Card("MCP server removed", new Text(message, CliTheme.Value), CliTheme.Success));
        }
        else
        {
            CliRender.Success(message);
        }

        return 0;
    }
}

/// <summary>
/// Reconnects one MCP server.
/// </summary>
public sealed class McpRestartCommand(CliHostResolver resolver, HostApiClient apiClient) : HostCommandBase<McpServerIdSettings>(resolver, apiClient)
{
    protected override async Task<int> RunAsync(HttpClient client, McpServerIdSettings settings, CancellationToken cancellationToken)
    {
        var serverId = McpCommandSupport.ParseServerId(settings.ServerId);
        using var response = await McpCommandSupport.ReconnectAsync(settings, $"Reconnecting MCP server {serverId}...",
            confirmed => client.PostAsync(ApiRoutes.Mcp.Restart(serverId) + $"?confirmed={confirmed.ToString().ToLowerInvariant()}",
                content: null, cancellationToken)).ConfigureAwait(false);
        McpCommandSupport.ThrowIfMissingServer(response, serverId);
        await CliHttpErrors.EnsureSuccessAsync(response, "Restarting MCP server", cancellationToken).ConfigureAwait(false);
        return await McpCommandSupport.ReportReconnectAsync(response, settings, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Reconnects every configured MCP server.
/// </summary>
public sealed class McpReconnectCommand(CliHostResolver resolver, HostApiClient apiClient) : HostCommandBase<McpReconnectSettings>(resolver, apiClient)
{
    protected override async Task<int> RunAsync(HttpClient client, McpReconnectSettings settings, CancellationToken cancellationToken)
    {
        using var response = await McpCommandSupport.ReconnectAsync(settings, "Reconnecting MCP servers...",
            confirmed => client.PostAsync(ApiRoutes.Mcp.ReconnectAll + $"?confirmed={confirmed.ToString().ToLowerInvariant()}",
                content: null, cancellationToken)).ConfigureAwait(false);
        await CliHttpErrors.EnsureSuccessAsync(response, "Reconnecting MCP servers", cancellationToken).ConfigureAwait(false);
        return await McpCommandSupport.ReportReconnectAsync(response, settings, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Accepts explicit consent to interrupt work during MCP reconnection.</summary>
public class McpReconnectSettings : OscovaCommandSettings
{
    [CommandOption("--yes")]
    [Description("Confirm interruption of work using the connections being restarted.")]
    public bool Yes { get; init; }
}

internal static class McpCommandSupport
{
    /// <summary>Retries a busy connection only after the caller explicitly accepts interruption.</summary>
    public static async Task<HttpResponseMessage> ReconnectAsync(McpReconnectSettings settings, string status,
        Func<bool, Task<HttpResponseMessage>> send)
    {
        var response = await CliRender.StatusAsync(status, settings, () => send(settings.Yes)).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Conflict || settings.Yes) { return response; }

        response.Dispose();
        CliRender.ConfirmDestructive(settings, false, "Reconnecting will stop work using these MCP connections.");
        return await CliRender.StatusAsync(status, settings, () => send(true)).ConfigureAwait(false);
    }

    /// <summary>
    /// Rejects a malformed server identifier before any request is sent.
    /// </summary>
    public static Guid ParseServerId(string value) => Guid.TryParse(value, out var serverId)
        ? serverId
        : throw new CliExitException(4, $"'{value}' is not a server id. Ids are GUIDs; list them with 'oscova mcp list'.");

    public static void ThrowIfMissingServer(HttpResponseMessage response, Guid serverId)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new CliExitException(4, $"No MCP server '{serverId}'. List servers with 'oscova mcp list'.");
        }
    }

    public static void ReportConnection(McpServerConnectionResult connection)
    {
        if (connection.IsConnected)
        {
            CliRender.Success($"{connection.ServerName}: connected, {connection.ToolCount} tool(s).");
            return;
        }

        CliRender.Warning($"{connection.ServerName}: not connected. {connection.Error}");
    }

    public static async Task<int> ReportReconnectAsync(HttpResponseMessage response, OscovaCommandSettings settings, CancellationToken cancellationToken)
    {
        var result = await response.Content.ReadFromJsonAsync<McpServerReconnectResult>(CliJson.Options, cancellationToken).ConfigureAwait(false)
            ?? new McpServerReconnectResult();

        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(result, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var rich = !settings.Quiet &&
            AnsiConsole.Profile.Capabilities.Interactive && AnsiConsole.Profile.Capabilities.Ansi;
        if (!rich)
        {
            foreach (var connection in result.Connections)
            {
                ReportConnection(connection);
            }

            return 0;
        }

        if (result.Connections.Count == 0)
        {
            AnsiConsole.Write(CliRender.Card(
                "MCP connections (0)",
                new Text("No MCP server connection results were returned.", CliTheme.Label),
                CliTheme.AccentDim));
            return 0;
        }

        var table = CliRender.Table("Server", "State", "Tools", "Detail");
        foreach (var connection in result.Connections)
        {
            table.AddRow(
                CliRender.Value(connection.ServerName),
                CliRender.State(connection.IsConnected ? "connected" : "not connected", connection.IsConnected),
                CliRender.Value(connection.ToolCount.ToString()),
                CliRender.Value(connection.Error ?? "-"));
        }

        CliRender.Separator($"MCP connections ({result.Connections.Count})");
        AnsiConsole.Write(table);
        return 0;
    }
}
