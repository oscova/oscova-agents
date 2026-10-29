using Oscova.Agents.App.Cli;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Oscova.Agents.App.Commands;

public sealed class LoginCommand(
    CliHostResolver resolver,
    CliDeviceLoginFlow loginFlow) : AsyncCommand<OscovaCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, OscovaCommandSettings settings, CancellationToken cancellationToken)
    {
        CliOutput.Configure(settings);
        var host = await resolver.ResolveAsync(settings, requireToken: false, cancellationToken).ConfigureAwait(false);
        var result = await loginFlow.LoginAsync(host, settings.Json, settings.Quiet, cancellationToken).ConfigureAwait(false);
        var message = $"Logged in to {result.HostUrl} as {result.Entry.IdentityUserName}.";
        if (!settings.Quiet && !settings.Json)
        {
            AnsiConsole.Write(CliRender.Card(
                "Signed in",
                new Text(message, new Style(CliTheme.Success)),
                CliTheme.Success));
        }

        return 0;
    }
}
