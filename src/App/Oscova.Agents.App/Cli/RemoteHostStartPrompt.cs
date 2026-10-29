using Spectre.Console;

namespace Oscova.Agents.App.Cli;

public sealed class RemoteHostStartPrompt(CliConfigStore configStore)
{
    public bool ShouldUseRemote(OscovaCommandSettings settings)
    {
        var host = settings.Host ?? Environment.GetEnvironmentVariable("OSCOVA_HOST") ?? configStore.Read().Host;
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var uri = new Uri(CliHostResolver.NormalizeHostUrl(host));
        return !uri.IsLoopback;
    }

    public static bool ConfirmStartLocal(OscovaCommandSettings settings)
    {
        if (settings.NoInteractive || settings.Json || settings.Quiet || Console.IsInputRedirected || !AnsiConsole.Profile.Capabilities.Interactive)
        {
            return true;
        }

        var selection = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("A remote host is configured. What should Oscova do?")
                .AddChoices("Start a local host", "Use the remote host"));

        return string.Equals(selection, "Start a local host", StringComparison.Ordinal);
    }
}
