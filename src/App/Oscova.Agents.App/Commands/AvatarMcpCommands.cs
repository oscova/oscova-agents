using System.ComponentModel;
using Oscova.Agents.App.Cli;
using Oscova.Agents.Infrastructure.Utils;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Oscova.Agents.App.Commands;

/// <summary>Selects the local MCP port and optional saved Avatar target.</summary>
public class AvatarMcpStartSettings : OscovaCommandSettings
{
    [CommandOption("--port <PORT>")]
    [Description("Localhost MCP port. Defaults to 11319.")]
    public int Port { get; init; } = 11319;

    [CommandOption("--avatar-url <URL>")]
    [Description("Save the Avatar bridge URL after MCP starts. Omit to reuse Avatar Settings.")]
    public string? AvatarUrl { get; init; }
}

/// <summary>Carries the parent's resolved installation identity into the detached child.</summary>
public sealed class AvatarMcpRunSettings : AvatarMcpStartSettings
{
    [CommandOption("--app-data <PATH>")]
    public string AppDataPath { get; init; } = string.Empty;

    [CommandOption("--instance-id <ID>")]
    public Guid InstanceId { get; init; }
}

/// <summary>Starts only the independently managed Avatar MCP listener.</summary>
public sealed class AvatarMcpStartCommand(AvatarMcpManager manager) : AsyncCommand<AvatarMcpStartSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, AvatarMcpStartSettings settings, CancellationToken cancellationToken)
    {
        CliOutput.Configure(settings);
        await CliRender.StatusAsync(
            "Starting Avatar MCP...", settings,
            () => manager.StartAsync(settings.Port, settings.AvatarUrl, cancellationToken)).ConfigureAwait(false);
        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(await manager.GetStatusAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }
        else if (!settings.Quiet && AnsiConsole.Profile.Capabilities.Interactive && AnsiConsole.Profile.Capabilities.Ansi)
        {
            CliRender.KeyValue("Avatar MCP ready",
            [
                ("Endpoint", CliRender.Value($"http://127.0.0.1:{settings.Port}/mcp")),
                ("Avatar connectivity", new Text("unchecked", CliTheme.Warning))
            ]);
        }
        else
        {
            CliRender.Success($"Avatar MCP is ready at http://127.0.0.1:{settings.Port}/mcp. Avatar connectivity has not been checked.");
        }

        return 0;
    }
}

/// <summary>Reports MCP readiness separately from downstream Avatar connectivity.</summary>
public sealed class AvatarMcpStatusCommand(AvatarMcpManager manager) : AsyncCommand<OscovaCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, OscovaCommandSettings settings, CancellationToken cancellationToken)
    {
        CliOutput.Configure(settings);
        var runtime = await manager.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var appData = runtime?.AppDataPath ?? PathUtil.AppDataPath;
        var avatarUrl = await manager.ReadAvatarUrlAsync(appData, cancellationToken).ConfigureAwait(false);
        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(new { running = runtime is not null, runtime, appDataPath = appData, avatarUrl, avatarConnectivity = "unchecked" }, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            CliRender.KeyValue("Avatar MCP",
            [
                ("Status", CliRender.State(runtime is null ? "stopped or not ready" : "running", runtime is not null)),
                ("Endpoint", CliRender.Value(runtime?.Endpoint ?? "-")),
                ("PID", CliRender.Value(runtime?.Pid.ToString() ?? "-")),
                ("App data", CliRender.Value(appData)),
                ("Avatar", CliRender.Value(avatarUrl)),
                ("Avatar connectivity", new Text("unchecked", CliTheme.Warning))
            ]);
        }

        return 0;
    }
}

/// <summary>Stops MCP without stopping the Avatar or removing its shared pairing.</summary>
public sealed class AvatarMcpStopCommand(AvatarMcpManager manager) : AsyncCommand<OscovaCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, OscovaCommandSettings settings, CancellationToken cancellationToken)
    {
        CliOutput.Configure(settings);
        await CliRender.StatusAsync(
            "Stopping Avatar MCP...", settings,
            () => manager.StopAsync(cancellationToken)).ConfigureAwait(false);
        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(new { running = false }, cancellationToken).ConfigureAwait(false);
        }
        else if (!settings.Quiet && AnsiConsole.Profile.Capabilities.Interactive && AnsiConsole.Profile.Capabilities.Ansi)
        {
            AnsiConsole.Write(CliRender.Card(
                "Avatar MCP stopped",
                new Text("Avatar settings and pairing were retained.", CliTheme.Value),
                CliTheme.Success));
        }
        else
        {
            CliRender.Success("Avatar MCP stopped. Avatar settings and pairing were retained.");
        }

        return 0;
    }
}

/// <summary>Runs the internal MCP child entry point in the existing Oscova executable.</summary>
public sealed class AvatarMcpRunCommand(AvatarMcpManager manager) : AsyncCommand<AvatarMcpRunSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, AvatarMcpRunSettings settings, CancellationToken cancellationToken)
    {
        CliOutput.Configure(settings);
        await manager.RunAsync(settings.Port, settings.AppDataPath, settings.InstanceId, settings.AvatarUrl, cancellationToken).ConfigureAwait(false);
        return 0;
    }
}
