using Oscova.Agents.App.Cli;
using Oscova.Agents.Core.Cli;
using System.Reflection;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Oscova.Agents.App.Commands;

/// <summary>
/// Runs the persistent Oscova gateway for plain `oscova`.
/// </summary>
public sealed class GatewayCommand(
    CliSurfaceInspector inspector,
    CliHostResolver resolver,
    HostApiClient apiClient,
    CliConfigStore configStore,
    CliAskRunner askRunner,
    DetachedHostLauncher launcher,
    CliDeviceLoginFlow loginFlow,
    CliAuthFileStore authStore,
    LocalHostControl localControl) : AsyncCommand<OscovaCommandSettings>
{
    public override async Task<int> ExecuteAsync(
        CommandContext context,
        OscovaCommandSettings settings,
        CancellationToken cancellationToken)
    {
        CliOutput.Configure(settings);
        var capabilities = AnsiConsole.Profile.Capabilities;
        var activity = new CliActivity(
            !settings.Json && !settings.Quiet && !settings.NoInteractive &&
            capabilities.Interactive && capabilities.Ansi,
            "Inspecting Oscova...");
        CliSurfaceReport report;
        try
        {
            report = await inspector.InspectAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await activity.StopAsync().ConfigureAwait(false);
        }

        if (settings.Json)
        {
            await WriteManifestAsync(report, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        if (settings.NoInteractive || settings.Quiet || Console.IsOutputRedirected || Console.IsInputRedirected)
        {
            await CliBranding.RenderIntroAsync(settings, cancellationToken).ConfigureAwait(false);
            RenderActions(settings);
            return 0;
        }

        await CliBranding.RenderIntroAsync(settings, cancellationToken).ConfigureAwait(false);

        var target = await PrepareInteractiveTargetAsync(settings, report, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            return 0;
        }

        while (true)
        {
            var session = new GatewaySession(
                resolver,
                apiClient,
                askRunner,
                inspector,
                configStore,
                localControl,
                target.Settings,
                target.Report,
                (currentReport, ct) => PromptRemoteFallbackAsync(target.Settings, currentReport, ct));
            try
            {
                return await session.RunAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (CliExitException ex) when (ex.ExitCode is 2 or 3)
            {
                var repairedTarget = await TryRepairGatewayLoginAsync(target, ex, cancellationToken).ConfigureAwait(false);
                if (repairedTarget is null)
                {
                    return 0;
                }

                target = repairedTarget;
            }
        }
    }

    private static async Task WriteManifestAsync(CliSurfaceReport report, CancellationToken cancellationToken)
    {
        await CliOutput.WriteJsonAsync(new
        {
            application = "oscova",
            version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "dev",
            target = new { host = report.TargetHostUrl, loopback = report.TargetIsLoopback },
            host = new { running = report.HostRunning, pid = report.Pid, urls = report.Urls },
            auth = new { loggedIn = report.Bearer.LoggedIn, user = report.Bearer.UserName, host = report.Bearer.HostUrl },
            localControl = new { available = report.LocalControlAvailable },
            paths = new { lifecycle = report.LifecyclePath, chat = report.ChatPath },
            commands = new[]
            {
                "start", "stop", "restart", "status", "login", "logout", "whoami", "ask",
                "threads", "agents", "models", "tasks", "plugins", "skills", "config"
            },
            exitCodes = new { internalError = 1, auth = 2, forbidden = 3, usage = 4 }
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GatewayTarget?> PrepareInteractiveTargetAsync(
        OscovaCommandSettings settings,
        CliSurfaceReport report,
        CancellationToken cancellationToken)
    {
        var activeSettings = settings;
        var activeReport = report;

        while (true)
        {
            try
            {
                if (activeReport.TargetIsLoopback && !activeReport.HostRunning)
                {
                    if (!AnsiConsole.Confirm("The Oscova host is not running. Start it now?", defaultValue: true))
                    {
                        CliOutput.Info("Run 'oscova start' when you are ready.", quiet: false);
                        return null;
                    }

                    await launcher.LaunchAsync(["start", "--no-interactive"], activeSettings, cancellationToken).ConfigureAwait(false);
                    activeReport = await inspector.InspectAsync(activeSettings, cancellationToken).ConfigureAwait(false);
                }

                if (!activeReport.Bearer.LoggedIn)
                {
                    if (!AnsiConsole.Confirm($"This terminal is not signed in to {activeReport.TargetHostUrl}. Log in now?", defaultValue: true))
                    {
                        CliOutput.Info($"Oscova is running at {activeReport.TargetHostUrl}. Run 'oscova login --host {activeReport.TargetHostUrl}' when you are ready.", quiet: false);
                        return null;
                    }

                    var host = await resolver.ResolveAsync(activeSettings, requireToken: false, cancellationToken).ConfigureAwait(false);
                    var result = await loginFlow.LoginAsync(host, jsonOutput: false, quiet: false, cancellationToken).ConfigureAwait(false);
                    CliOutput.Info($"Logged in to {result.HostUrl} as {result.Entry.IdentityUserName}.", quiet: false);
                    activeSettings = WithHost(activeSettings, result.HostUrl);
                    activeReport = await inspector.InspectAsync(activeSettings, cancellationToken).ConfigureAwait(false);
                }

                return new GatewayTarget(activeSettings, activeReport);
            }
            catch (Exception ex) when (IsConnectionFailure(ex, cancellationToken) && !activeReport.TargetIsLoopback)
            {
                var fallback = await PromptRemoteFallbackAsync(activeSettings, activeReport, cancellationToken).ConfigureAwait(false);
                if (fallback is null)
                {
                    activeReport = await inspector.InspectAsync(activeSettings, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                activeSettings = fallback.Settings;
                activeReport = fallback.Report;
            }
        }
    }

    private async Task<GatewayTarget?> PromptRemoteFallbackAsync(
        OscovaCommandSettings originalSettings,
        CliSurfaceReport report,
        CancellationToken cancellationToken)
    {
        if (report.TargetIsLoopback)
        {
            return null;
        }

        var selection = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"Could not reach {report.TargetHostUrl}. What should Oscova do?")
                .AddChoices("Use the remote host", "Start a local host"));

        if (string.Equals(selection, "Use the remote host", StringComparison.Ordinal))
        {
            return null;
        }

        var localSettings = WithHost(originalSettings, CliHostUrlSelector.DefaultLocalHost);
        var localReport = await inspector.InspectAsync(localSettings, cancellationToken).ConfigureAwait(false);
        if (!localReport.HostRunning)
        {
            await launcher.LaunchAsync(["start", "--no-interactive"], localSettings, cancellationToken).ConfigureAwait(false);
            localReport = await inspector.InspectAsync(localSettings, cancellationToken).ConfigureAwait(false);
        }

        if (!localReport.Bearer.LoggedIn)
        {
            if (!AnsiConsole.Confirm($"This terminal is not signed in to {localReport.TargetHostUrl}. Log in now?", defaultValue: true))
            {
                CliOutput.Info("Run 'oscova login' to enable chat.", quiet: false);
                return null;
            }

            var host = await resolver.ResolveAsync(localSettings, requireToken: false, cancellationToken).ConfigureAwait(false);
            var result = await loginFlow.LoginAsync(host, jsonOutput: false, quiet: false, cancellationToken).ConfigureAwait(false);
            CliOutput.Info($"Logged in to {result.HostUrl} as {result.Entry.IdentityUserName}.", quiet: false);
            localSettings = WithHost(localSettings, result.HostUrl);
            localReport = await inspector.InspectAsync(localSettings, cancellationToken).ConfigureAwait(false);
        }

        return new GatewayTarget(localSettings, localReport);
    }

    private async Task<GatewayTarget?> TryRepairGatewayLoginAsync(
        GatewayTarget target,
        CliExitException exception,
        CancellationToken cancellationToken)
    {
        CliOutput.Error(exception.Message);
        await authStore.RemoveAsync(target.Report.TargetHostUrl, cancellationToken).ConfigureAwait(false);
        if (!AnsiConsole.Confirm($"Log in to {target.Report.TargetHostUrl} now?", defaultValue: true))
        {
            CliOutput.Info($"Oscova is running at {target.Report.TargetHostUrl}. Run 'oscova login --host {target.Report.TargetHostUrl}' when you are ready.", quiet: false);
            return null;
        }

        var host = await resolver.ResolveAsync(target.Settings, requireToken: false, cancellationToken).ConfigureAwait(false);
        var result = await loginFlow.LoginAsync(host, jsonOutput: false, quiet: false, cancellationToken).ConfigureAwait(false);
        CliOutput.Info($"Logged in to {result.HostUrl} as {result.Entry.IdentityUserName}.", quiet: false);
        var settings = WithHost(target.Settings, result.HostUrl);
        return new GatewayTarget(settings, await inspector.InspectAsync(settings, cancellationToken));
    }

    private static OscovaCommandSettings WithHost(OscovaCommandSettings settings, string host) => new()
    {
        Host = host,
        Quiet = settings.Quiet,
        NoColor = settings.NoColor,
        NoInteractive = settings.NoInteractive,
        Json = settings.Json
    };

    private static bool IsConnectionFailure(Exception exception, CancellationToken cancellationToken)
    {
        return exception is HttpRequestException { StatusCode: null } ||
            exception is TaskCanceledException && !cancellationToken.IsCancellationRequested;
    }

    private static void RenderActions(OscovaCommandSettings settings)
    {
        if (settings.Quiet)
        {
            return;
        }

        Table table;
        if (AnsiConsole.Profile.Capabilities.Interactive && AnsiConsole.Profile.Capabilities.Ansi)
        {
            table = CliRender.Table("Command", "Description");
            table.AddRow(CliRender.Value("oscova start"), CliRender.Value("Launch the Oscova host"));
            table.AddRow(CliRender.Value("oscova ask \"...\""), CliRender.Value("Ask an agent from the terminal"));
            table.AddRow(CliRender.Value("oscova login"), CliRender.Value("Sign in this CLI to a host"));
            table.AddRow(CliRender.Value("oscova status"), CliRender.Value("Show host status"));
        }
        else
        {
            table = new Table()
                .NoBorder()
                .HideHeaders()
                .AddColumn("Command")
                .AddColumn("Description");

            table.AddRow("[grey70]oscova start[/]", "Launch the Oscova host");
            table.AddRow("[grey70]oscova ask \"...\"[/]", "Ask an agent from the terminal");
            table.AddRow("[grey70]oscova login[/]", "Sign in this CLI to a host");
            table.AddRow("[grey70]oscova status[/]", "Show host status");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(table);
    }
}
