using Oscova.Agents.App.Cli;
using Spectre.Console;
using Spectre.Console.Cli;
using System.Net;

namespace Oscova.Agents.App.Commands;

public sealed class LogoutCommand(CliHostResolver resolver, HostApiClient apiClient, CliAuthFileStore authStore) : AsyncCommand<OscovaCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, OscovaCommandSettings settings, CancellationToken cancellationToken)
    {
        CliOutput.Configure(settings);
        var host = await resolver.ResolveAsync(settings, requireToken: false, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(host.Token))
        {
            using var client = apiClient.Create(host);
            try
            {
                using var response = await CliRender.StatusAsync(
                    "Revoking CLI login...", settings,
                    () => client.PostAsync("api/auth/cli/revoke", null, cancellationToken)).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Unauthorized)
                {
                    throw new CliExitException(1, $"The host returned {(int)response.StatusCode} while revoking this CLI login. Local auth was not removed.");
                }
            }
            catch (HttpRequestException)
            {
            }
        }

        await authStore.RemoveAsync(host.HostUrl, cancellationToken).ConfigureAwait(false);
        const string message = "Logged out.";
        // Requested JSON results remain available when informational output is quiet.
        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(new { success = true, message }, cancellationToken).ConfigureAwait(false);
        }
        else if (!settings.Quiet &&
            AnsiConsole.Profile.Capabilities.Interactive && AnsiConsole.Profile.Capabilities.Ansi)
        {
            AnsiConsole.Write(CliRender.Card("CLI login", new Text(message, CliTheme.Value), CliTheme.AccentDim));
        }
        else
        {
            CliOutput.Info(message, settings.Quiet);
        }

        return 0;
    }
}
