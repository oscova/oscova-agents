using System.ComponentModel;
using Oscova.Agents.App.Cli;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Oscova.Agents.App.Commands;

/// <summary>Selects the source for Avatar Engine installation.</summary>
public sealed class AvatarInstallSettings : OscovaCommandSettings
{
    [CommandOption("--source <ZIP>")]
    [Description("Local Avatar Engine ZIP. Omit to download the published release.")]
    public string? Source { get; init; }

    [CommandOption("--yes")]
    [Description("Confirm replacement of a changed, unknown, or incomplete Avatar installation.")]
    public bool Yes { get; init; }

}

/// <summary>Provides the bridge binding used to start the Avatar Engine.</summary>
public sealed class AvatarStartSettings : OscovaCommandSettings
{
    [CommandOption("--address <ADDRESS>")]
    [Description("Bridge IP address to bind. Defaults to 0.0.0.0 for a trusted LAN. Use 127.0.0.1 for local access only.")]
    public string Address { get; init; } = "0.0.0.0";

    [CommandOption("--port <PORT>")]
    [Description("Bridge port to bind.")]
    public int Port { get; init; } = 11318;
}

/// <summary>Downloads or installs the Avatar Engine.</summary>
public sealed class AvatarInstallCommand(AvatarEngineManager manager) : AsyncCommand<AvatarInstallSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, AvatarInstallSettings settings, CancellationToken cancellationToken)
    {
        CliOutput.Configure(settings);
        var check = await CliRender.StatusAsync("Checking Avatar Engine release...", settings,
            () => manager.CheckInstallationAsync(settings.Source, cancellationToken)).ConfigureAwait(false);
        if (check.State == "current")
        {
            if (settings.Json) { await CliOutput.WriteJsonAsync(new { installed = true, state = "current", release = check.Installed }, cancellationToken).ConfigureAwait(false); }
            else { CliOutput.Info("Avatar Engine is current.", settings.Quiet); }

            return 0;
        }

        if (!settings.Json && !settings.Quiet) { CliRender.AvatarInstallNotice(); }

        if (check.State != "absent" && !settings.Yes)
        {
            if (settings.Json || settings.Quiet || settings.NoInteractive || Console.IsInputRedirected || !AnsiConsole.Profile.Capabilities.Interactive)
            {
                throw new CliExitException(4, "Avatar Engine replacement requires --yes when the CLI cannot prompt.");
            }

            var reason = check.State switch
            {
                "unknown" => "The installed Avatar Engine version is unknown.",
                "incomplete" => "The Avatar Engine installation is incomplete.",
                "changed-release" => "The published Avatar Engine release changed.",
                _ => "An Avatar Engine update is available."
            };
            CliRender.ConfirmDestructive(settings, false, reason + " Replace the installed engine?");
        }

        await CliRender.RunAvatarInstallAsync(
            (progress, token) => manager.InstallAsync(settings.Source, token, progress, replace: check.State != "absent" || settings.Yes),
            settings,
            cancellationToken).ConfigureAwait(false);

        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(new { installed = true }, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        if (!settings.Quiet && AnsiConsole.Profile.Capabilities.Interactive && AnsiConsole.Profile.Capabilities.Ansi)
        {
            CliRender.KeyValue("Avatar Engine installed",
            [
                ("Start", CliRender.Value("oscova avatar start")),
                ("Local only", CliRender.Value("oscova avatar start --address 127.0.0.1")),
                ("Avatar Settings", CliRender.Value("Enter this renderer's LAN IP and port 11318."))
            ]);
        }
        else
        {
            CliOutput.Info("Avatar Engine installed.", settings.Quiet);
            CliOutput.Info("Start: oscova avatar start", settings.Quiet);
            CliOutput.Info("Local only: oscova avatar start --address 127.0.0.1", settings.Quiet);
            CliOutput.Info("Then enter this renderer's LAN IP and port 11318 in Avatar Settings.", settings.Quiet);
        }

        return 0;
    }
}

/// <summary>Starts the installed Avatar Engine independently from the Oscova Host.</summary>
public sealed class AvatarStartCommand(AvatarEngineManager manager) : AsyncCommand<AvatarStartSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, AvatarStartSettings settings, CancellationToken cancellationToken)
    {
        CliOutput.Configure(settings);
        var runtime = await CliRender.StatusAsync(
            "Starting Avatar Engine...", settings,
            () => manager.StartAsync(settings.Address, settings.Port, cancellationToken)).ConfigureAwait(false);
        var availability = await manager.CheckAvailabilityAsync(cancellationToken).ConfigureAwait(false);

        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(new { runtime.Pid, runtime.StartedAtUtc, runtime.Address, runtime.Port, availability }, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        if (!settings.Quiet && AnsiConsole.Profile.Capabilities.Interactive && AnsiConsole.Profile.Capabilities.Ansi)
        {
            CliRender.KeyValue("Avatar Engine started",
            [
                ("Bridge", CliRender.Value($"{runtime.Address}:{runtime.Port}")),
                ("PID", CliRender.Value(runtime.Pid.ToString()))
            ]);
        }
        else
        {
            CliOutput.Info($"Avatar Engine started at {runtime.Address}:{runtime.Port} (pid {runtime.Pid}).", settings.Quiet);
        }

        CliOutput.Info($"Installed version: {availability.Installed?.Version ?? "unknown"}.", settings.Quiet);
        var notice = availability.State switch
        {
            "current" => "Avatar Engine is current.",
            "check-unavailable" => "Avatar update check unavailable. The installed engine is running.",
            "unknown" => "Installed Avatar identity is unknown. Check with 'oscova avatar install'.",
            "changed-release" => "The published Avatar release changed. Run 'oscova avatar stop', then 'oscova avatar install'.",
            _ => "An Avatar Engine update is available. Run 'oscova avatar stop', then 'oscova avatar install'."
        };
        CliOutput.Info(notice, settings.Quiet);

        return 0;
    }
}

/// <summary>Shows whether the Avatar Engine is supported, installed, and running.</summary>
public sealed class AvatarStatusCommand(AvatarEngineManager manager) : AsyncCommand<OscovaCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, OscovaCommandSettings settings, CancellationToken cancellationToken)
    {
        CliOutput.Configure(settings);
        var status = await manager.GetStatusAsync(cancellationToken).ConfigureAwait(false);

        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(status, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        CliRender.KeyValue("Avatar Engine",
        [
            ("Platform", CliRender.State(status.Supported ? "supported" : "Windows required", status.Supported)),
            ("Installed", CliRender.State(status.Installed ? "yes" : "no", status.Installed)),
            ("Version", CliRender.Value(status.Release?.Version ?? "unknown")),
            ("Status", CliRender.Status(status.Running ? "running" : "stopped")),
            ("PID", CliRender.Value(status.Pid?.ToString() ?? "-")),
            ("Bridge", CliRender.Value(status.Address is null ? "-" : $"{status.Address}:{status.Port}"))
        ]);
        return 0;
    }
}

/// <summary>Stops the Avatar Engine process started by Oscova.</summary>
public sealed class AvatarStopCommand(AvatarEngineManager manager) : AsyncCommand<OscovaCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, OscovaCommandSettings settings, CancellationToken cancellationToken)
    {
        CliOutput.Configure(settings);
        await CliRender.StatusAsync(
            "Stopping Avatar Engine...", settings,
            () => manager.StopAsync(cancellationToken)).ConfigureAwait(false);

        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(new { running = false }, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        if (!settings.Quiet && AnsiConsole.Profile.Capabilities.Interactive && AnsiConsole.Profile.Capabilities.Ansi)
        {
            AnsiConsole.Write(CliRender.Card(
                "Avatar Engine",
                new Text("Avatar Engine stopped.", CliTheme.Value),
                CliTheme.Success));
        }
        else
        {
            CliRender.Success("Avatar Engine stopped.");
        }

        return 0;
    }
}

/// <summary>Stops and removes the locally installed Avatar Engine.</summary>
public sealed class AvatarUninstallCommand(AvatarEngineManager manager) : AsyncCommand<OscovaCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, OscovaCommandSettings settings, CancellationToken cancellationToken)
    {
        CliOutput.Configure(settings);
        await CliRender.StatusAsync(
            "Uninstalling Avatar Engine...", settings,
            () => manager.UninstallAsync(cancellationToken)).ConfigureAwait(false);

        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(new { installed = false }, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        if (!settings.Quiet && AnsiConsole.Profile.Capabilities.Interactive && AnsiConsole.Profile.Capabilities.Ansi)
        {
            AnsiConsole.Write(CliRender.Card(
                "Avatar Engine",
                new Text("Avatar Engine uninstalled.", CliTheme.Value),
                CliTheme.Success));
        }
        else
        {
            CliRender.Success("Avatar Engine uninstalled.");
        }

        return 0;
    }
}
