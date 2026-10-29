using System.Net;
using System.Net.Http.Json;
using Oscova.Agents.App.Cli;
using Oscova.Agents.Core.ApiContracts.HostControl;
using Oscova.Agents.Host.Constants;
using Spectre.Console.Cli;

namespace Oscova.Agents.App.Commands;

public sealed class StatusCommand(
    CliHostResolver resolver,
    HostApiClient apiClient,
    LocalHostControl localControl,
    CliSurfaceInspector inspector) : AsyncCommand<OscovaCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, OscovaCommandSettings settings, CancellationToken cancellationToken)
    {
        CliOutput.Configure(settings);
        return await RenderCurrentStatusAsync(resolver, apiClient, localControl, inspector, settings, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<int> RenderCurrentStatusAsync(
        CliHostResolver resolver,
        HostApiClient apiClient,
        LocalHostControl localControl,
        CliSurfaceInspector inspector,
        OscovaCommandSettings settings,
        CancellationToken cancellationToken)
    {
        var report = await inspector.InspectAsync(settings, cancellationToken, strictHostProbe: true).ConfigureAwait(false);
        if (report.TargetIsLoopback && !report.HostRunning)
        {
            // A stopped local host cannot provide the live status document.
            return await RenderStatusAsync(report.TargetHostUrl, null, report, settings, cancellationToken).ConfigureAwait(false);
        }

        var local = await localControl.TryResolveLocalAsync(settings, cancellationToken).ConfigureAwait(false);
        if (local is not null)
        {
            HostStatusResponse localStatus;
            try
            {
                localStatus = await localControl.StatusAsync(local, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception) when (
                exception.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError)
            {
                throw new CliExitException(1, "The Oscova host is not running. Run 'oscova start' to start it.");
            }

            return await RenderStatusAsync(LocalHostControl.SelectLocalHost(local), localStatus, report, settings, cancellationToken).ConfigureAwait(false);
        }

        var host = await resolver.ResolveAsync(settings, requireToken: true, cancellationToken).ConfigureAwait(false);
        using var client = apiClient.Create(host);
        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(ApiRoutes.HostControl.Status, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception) when (
            exception.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError)
        {
            var message = report.TargetIsLoopback
                ? "The Oscova host is not running. Run 'oscova start' to start it."
                : $"Could not reach the Oscova host at {host.HostUrl}.";
            throw new CliExitException(1, message);
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new CliExitException(3, "This CLI login is not authorized to read host status.");
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new CliExitException(2, $"Your CLI login for {host.HostUrl} is no longer valid. Run 'oscova login --host {host.HostUrl}' again.");
        }

        response.EnsureSuccessStatusCode();
        var status = await response.Content.ReadFromJsonAsync<HostStatusResponse>(CliJson.Options, cancellationToken)
.ConfigureAwait(false) ?? throw new CliExitException(1, "The host returned an empty status response.");

        return await RenderStatusAsync(host.HostUrl, status, report, settings, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> RenderStatusAsync(
        string hostUrl,
        HostStatusResponse? status,
        CliSurfaceReport report,
        OscovaCommandSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(new { host = status, surfaces = report }, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        CliRender.KeyValue("Host status",
        [
            ("Host", CliRender.Value(hostUrl)),
            ("Target host", CliRender.Value(report.TargetIsLoopback ? report.TargetHostUrl : $"{report.TargetHostUrl} (remote)")),
            ("State", CliRender.Status(status is null ? "stopped" : "running")),
            ("PID", CliRender.Value(status?.Pid.ToString() ?? "-")),
            ("User", CliRender.Value(status?.CurrentUserName ?? "-")),
            ("URLs", CliRender.Value(status is null ? "-" : string.Join(Environment.NewLine, status.Urls))),
            ("Local control", CliRender.State(report.LocalControlAvailable ? "available" : "not available", report.LocalControlAvailable)),
            ("CLI login", CliRender.State(FormatBearer(report.Bearer), report.Bearer.LoggedIn)),
            ("Lifecycle via", CliRender.Value(report.LifecyclePath)),
            ("Chat via", CliRender.Value(report.ChatPath)),
            ("Restart required", status is null
                ? CliRender.Value("-")
                : CliRender.State(status.RestartRequired ? "yes" : "no", !status.RestartRequired))
        ]);

        return 0;
    }

    private static string FormatBearer(CliBearerState bearer)
    {
        if (!bearer.LoggedIn)
        {
            return "not logged in";
        }

        var expiry = bearer.ExpiresAtUtc is null
            ? "no expiry"
            : $"expires {bearer.ExpiresAtUtc:yyyy-MM-dd HH:mm} UTC";
        return $"{bearer.UserName} at {bearer.HostUrl} ({expiry})";
    }
}
