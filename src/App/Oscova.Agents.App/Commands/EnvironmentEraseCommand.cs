using System.ComponentModel;
using Oscova.Agents.App.Cli;
using Oscova.Agents.Core.Cli;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Oscova.Agents.App.Commands;

/// <summary>Requires explicit confirmation before permanently removing this user's owned Oscova data.</summary>
public sealed class EnvironmentEraseCommand(CliPaths paths, EnvironmentEraseOperation operation) : AsyncCommand<EnvironmentEraseCommand.Settings>
{
    /// <summary>Supplies both confirmations required for unattended complete erasure.</summary>
    public sealed class Settings : OscovaCommandSettings
    {
        [CommandOption("--yes")]
        [Description("Confirm complete erasure together with --confirm ERASE.")]
        public bool Yes { get; init; }

        [CommandOption("--confirm <WORD>")]
        [Description("Supply the literal ERASE together with --yes for unattended erasure.")]
        public string? Confirm { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        CliOutput.Configure(settings);
        var environmentHost = Environment.GetEnvironmentVariable("OSCOVA_HOST");
        if (settings.Host is not null || !string.IsNullOrWhiteSpace(environmentHost) &&
            !CliHostUrlSelector.IsLoopback(CliHostResolver.NormalizeHostUrl(environmentHost)))
        {
            throw new CliExitException(4, "Environment erase is local only. Remove --host and any remote OSCOVA_HOST setting.");
        }

        var preview = operation.Preview();
        var existing = preview.Where(target => Directory.Exists(target.Path)).Select(target => target.Path).ToArray();
        const string consequence = "Permanently erase all Oscova accounts, credentials, agents, files, plugins, models, Avatar installation and saved state, and recovery data. There is no backup.";
        if (!settings.Json && !settings.Quiet)
        {
            CliRender.KeyValue("Erase Oscova environment", existing.Select(path => ("Delete", path)).ToArray());
            CliOutput.Info(consequence);
            CliOutput.Info("The .NET tool, external workspaces, shared caches, manual exports, and unselected environments are excluded.");
        }

        var explicitConfirmation = settings.Yes && string.Equals(settings.Confirm, "ERASE", StringComparison.Ordinal);
        if (!explicitConfirmation)
        {
            if (settings.Yes || settings.Confirm is not null || settings.Json || settings.Quiet || settings.NoInteractive ||
                Console.IsInputRedirected || !AnsiConsole.Profile.Capabilities.Interactive)
            {
                throw new CliExitException(4, "Complete erase requires both --yes --confirm ERASE when not confirming interactively.");
            }

            CliRender.ConfirmDestructive(settings, false, consequence);
            if (!string.Equals(AnsiConsole.Prompt(new TextPrompt<string>("Type ERASE to permanently delete these targets:")), "ERASE", StringComparison.Ordinal))
            {
                throw new CliExitException(4, "Cancelled. The confirmation must be exactly ERASE.");
            }
        }

        using var lease = LocalLifecycleLease.TryAcquire(paths.RootDirectory)
            ?? throw new CliExitException(1, "Oscova startup or installation is still in progress. Try again after it finishes.");
        var removed = await CliRender.StatusAsync("Stopping Oscova and erasing owned data...", settings,
            () => operation.StopAndEraseAsync(preview, cancellationToken)).ConfigureAwait(false);
        const string message = "Oscova environment erased. Run 'oscova start' to create a fresh environment.";
        if (settings.Json) { await CliOutput.WriteJsonAsync(new { erased = true, removed, message }, cancellationToken).ConfigureAwait(false); }
        else if (!settings.Quiet) { CliRender.Success(message); }

        return 0;
    }
}
