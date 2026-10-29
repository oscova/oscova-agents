using System.Net;
using Oscova.Agents.App.Cli;
using Oscova.Agents.Core.Cli;
using Oscova.Agents.Host.Hosting;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Oscova.Agents.App.Commands;

public sealed class StopCommand(
    CliHostResolver resolver,
    HostApiClient apiClient,
    LocalHostControl localControl,
    CliSurfaceInspector inspector,
    CliPaths paths) : AsyncCommand<OscovaCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, OscovaCommandSettings settings, CancellationToken cancellationToken)
    {
        CliOutput.Configure(settings);

        var local = await localControl.TryResolveLocalAsync(settings, cancellationToken, allowUnresponsive: true).ConfigureAwait(false);
        var selected = await resolver.ResolveAsync(settings, requireToken: false, cancellationToken).ConfigureAwait(false);
        if (!CliHostUrlSelector.IsLoopback(selected.HostUrl)) { local = null; }

        var ownedLocal = local is not null;
        if (!ownedLocal)
        {
            CliSurfaceReport report;
            try { report = await inspector.InspectAsync(settings, cancellationToken, strictHostProbe: true).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new CliExitException(1, "The Host status check timed out. Shutdown could not be confirmed. Check 'oscova status' before trying again.");
            }

            var descriptor = RuntimeDescriptorWriter.ReadOrNull();
            ownedLocal = report.TargetIsLoopback && !report.HostRunning &&
                (descriptor is null || CliHostUrlSelector.PortMatchesAny(report.TargetHostUrl, descriptor.Urls)) &&
                CliHostUrlSelector.PortMatchesAny(report.TargetHostUrl, [CliHostUrlSelector.DefaultLocalHost]);
        }

        if (ownedLocal)
        {
            // Shutdown cancels Host-owned downloads before waiting on their lifecycle lease.
            var stoppedHost = local is not null;
            if (local is not null) { await localControl.StopAsync(local, cancellationToken).ConfigureAwait(false); }

            using var lease = await LocalLifecycleLease.AcquireAsync(paths.RootDirectory, cancellationToken).ConfigureAwait(false);
            local = await localControl.TryResolveLocalAsync(settings, cancellationToken, allowUnresponsive: true).ConfigureAwait(false);
            var descriptor = RuntimeDescriptorWriter.ReadOrNull();
            if (local is null && descriptor is not null && !CliHostUrlSelector.PortMatchesAny(selected.HostUrl, descriptor.Urls))
            {
                throw new CliExitException(1, "The local Host uses a different port. Run 'oscova status' and select its address before stopping it.");
            }

            var stopped = await CliRender.StatusAsync(
                "Stopping Oscova Host and Avatar MCP...", settings,
                () => localControl.StopLocalServicesAsync(local, cancellationToken)).ConfigureAwait(false);
            await RenderResultAsync(settings, stopped || stoppedHost ? "Oscova Host and Avatar MCP are stopped." : "Oscova is already stopped.", "stopped", cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var host = await resolver.ResolveAsync(settings, requireToken: true, cancellationToken).ConfigureAwait(false);
        using var client = apiClient.Create(host);
        HttpResponseMessage response;
        try
        {
            response = await CliRender.StatusAsync(
                "Requesting host stop...", settings,
                () => client.PostAsync("api/host/shutdown", null, cancellationToken)).ConfigureAwait(false);
        }
        catch (HttpRequestException exception) when (
            exception.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError)
        {
            throw new CliExitException(1, $"Could not reach the Oscova host at {host.HostUrl}. It may already be stopped.");
        }

        using var ownedResponse = response;
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new CliExitException(3, "Only an admin CLI login can stop the host.");
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new CliExitException(2, $"Your CLI login for {host.HostUrl} is no longer valid. Run 'oscova login --host {host.HostUrl}' again.");
        }

        response.EnsureSuccessStatusCode();
        await RenderResultAsync(settings, "Stop requested.", "requested", cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private static async Task RenderResultAsync(OscovaCommandSettings settings, string message, string state, CancellationToken ct)
    {
        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(new { state, message }, ct).ConfigureAwait(false);
        }
        else if (!settings.Quiet &&
            AnsiConsole.Profile.Capabilities.Interactive && AnsiConsole.Profile.Capabilities.Ansi)
        {
            AnsiConsole.Write(CliRender.Card("Host stop", new Text(message, CliTheme.Value), CliTheme.AccentDim));
        }
        else
        {
            CliOutput.Info(message, settings.Quiet);
        }
    }
}
