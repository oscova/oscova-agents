using System.Net;
using System.Text.Json;
using Oscova.Agents.App.Cli;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Oscova.Agents.App.Commands;

public sealed class RestartCommand(
    CliHostResolver resolver,
    HostApiClient apiClient,
    LocalHostControl localControl) : AsyncCommand<OscovaCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, OscovaCommandSettings settings, CancellationToken cancellationToken)
    {
        CliOutput.Configure(settings);

        var local = await localControl.TryResolveLocalAsync(settings, cancellationToken).ConfigureAwait(false);
        if (local is not null)
        {
            await CliRender.StatusAsync(
                "Requesting host restart...", settings,
                () => localControl.RestartAsync(local, cancellationToken)).ConfigureAwait(false);
            await RenderResultAsync(settings, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var host = await resolver.ResolveAsync(settings, requireToken: true, cancellationToken).ConfigureAwait(false);
        using var client = apiClient.Create(host);
        using var response = await CliRender.StatusAsync(
            "Requesting host restart...", settings,
            () => client.PostAsync("api/host/restart", null, cancellationToken)).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new CliExitException(3, "Only an admin CLI login can restart the host.");
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new CliExitException(2, $"Your CLI login for {host.HostUrl} is no longer valid. Run 'oscova login --host {host.HostUrl}' again.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var message = await ReadHostErrorAsync(response, cancellationToken)
.ConfigureAwait(false) ?? $"The host returned {(int)response.StatusCode} while restarting.";
            throw new CliExitException(1, message);
        }

        await RenderResultAsync(settings, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private static async Task RenderResultAsync(OscovaCommandSettings settings, CancellationToken ct)
    {
        const string message = "Restart requested.";
        // Restart acceptance is requested data, even in quiet mode.
        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(new { state = "requested", message }, ct).ConfigureAwait(false);
        }
        else if (!settings.Quiet &&
            AnsiConsole.Profile.Capabilities.Interactive && AnsiConsole.Profile.Capabilities.Ansi)
        {
            AnsiConsole.Write(CliRender.Card("Host restart", new Text(message, CliTheme.Value), CliTheme.AccentDim));
        }
        else
        {
            CliOutput.Info(message, settings.Quiet);
        }
    }

    private static async Task<string?> ReadHostErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            if (raw.TrimStart().StartsWith('"'))
            {
                return JsonSerializer.Deserialize<string>(raw);
            }

            var error = JsonSerializer.Deserialize<HostErrorResponse>(raw, CliJson.Options);
            if (!string.IsNullOrWhiteSpace(error?.Detail))
            {
                return error.Detail;
            }

            if (!string.IsNullOrWhiteSpace(error?.Message))
            {
                return error.Message;
            }
        }
        catch (JsonException)
        {
            return raw;
        }

        return raw;
    }

    private sealed record HostErrorResponse(string? Detail, string? Message);
}
