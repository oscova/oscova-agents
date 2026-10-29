using System.ComponentModel;
using Oscova.Agents.App.Cli;
using Oscova.Agents.Host.Constants;
using Oscova.Agents.Infrastructure.Backup;
using Oscova.Agents.Infrastructure.Updates;
using Oscova.Agents.Core.Backup;
using Oscova.Agents.Core.Cli;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Oscova.Agents.App.Commands;

public sealed class EnvironmentExportSettings : OscovaCommandSettings
{
    [CommandOption("--out <PATH>")]
    [Description("File to write the backup to. Defaults to the name the host suggests, in the current directory.")]
    public string? Out { get; init; }

    [CommandOption("--passphrase <PASSPHRASE>")]
    [Description("Passphrase that seals the backup. Prompted for when omitted.")]
    public string? Passphrase { get; init; }
}

/// <summary>
/// Writes a passphrase-sealed backup of the whole environment. Requires an administrator account.
/// </summary>
public sealed class EnvironmentExportCommand(CliHostResolver resolver, HostApiClient apiClient) : HostCommandBase<EnvironmentExportSettings>(resolver, apiClient)
{
    protected override async Task<int> RunAsync(HttpClient client, EnvironmentExportSettings settings, CancellationToken cancellationToken)
    {
        var passphrase = EnvironmentCommandSupport.ResolvePassphrase(settings, settings.Passphrase);
        using var content = new MultipartFormDataContent { { new StringContent(passphrase), "passphrase" } };

        using var request = new HttpRequestMessage(HttpMethod.Post, ApiRoutes.Environment.Export) { Content = content };
        using var response = await CliRender.StatusAsync(
            "Exporting environment...", settings,
            () => client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)).ConfigureAwait(false);
        await CliHttpErrors.EnsureSuccessAsync(response, "Exporting the environment", cancellationToken).ConfigureAwait(false);

        var path = settings.Out ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? "oscova-environment.oscb";
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var target = File.Create(path))
        {
            await CliRender.StatusAsync(
                "Writing environment backup...", settings,
                () => source.CopyToAsync(target, cancellationToken)).ConfigureAwait(false);
        }

        var message = $"Wrote {Path.GetFullPath(path)}.";
        if (!settings.Json && !settings.Quiet &&
            AnsiConsole.Profile.Capabilities.Interactive && AnsiConsole.Profile.Capabilities.Ansi)
        {
            AnsiConsole.Write(CliRender.Card("Environment exported", new Text(message, CliTheme.Value), CliTheme.Success));
        }
        else
        {
            CliRender.Success(message);
        }

        return 0;
    }
}

public sealed class EnvironmentRestoreSettings : OscovaCommandSettings
{
    [CommandOption("--file <PATH>")]
    [Description("Backup file to restore. Required.")]
    public string? File { get; init; }

    [CommandOption("--passphrase <PASSPHRASE>")]
    [Description("Passphrase the backup was sealed with. Prompted for when omitted.")]
    public string? Passphrase { get; init; }

    [CommandOption("--yes")]
    [Description("Restore without asking for confirmation.")]
    public bool Yes { get; init; }
}

/// <summary>
/// Restores a backup over a fresh install. The host restarts once the backup is staged.
/// </summary>
public sealed class EnvironmentRestoreCommand(CliHostResolver resolver, HostApiClient apiClient) : HostCommandBase<EnvironmentRestoreSettings>(resolver, apiClient)
{
    // Restore is the fresh-install path, so there is no account to sign in with yet.
    protected override bool RequireToken => false;

    protected override async Task<int> RunAsync(HttpClient client, EnvironmentRestoreSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.File))
        {
            throw new CliExitException(4, "--file is required.");
        }

        if (!File.Exists(settings.File))
        {
            throw new CliExitException(4, $"No file at '{settings.File}'.");
        }

        if (!settings.Yes && !settings.Json && !settings.Quiet &&
            AnsiConsole.Profile.Capabilities.Interactive && AnsiConsole.Profile.Capabilities.Ansi)
        {
            CliRender.KeyValue("Environment restore",
            [
                ("Backup", CliRender.Value(Path.GetFullPath(settings.File)))
            ]);
        }

        CliRender.ConfirmDestructive(settings, settings.Yes, "The host will be overwritten with this backup and then restarted.");
        var passphrase = EnvironmentCommandSupport.ResolvePassphrase(settings, settings.Passphrase);

        await using var stream = File.OpenRead(settings.File);
        using var content = new MultipartFormDataContent
        {
            { new StreamContent(stream), "file", Path.GetFileName(settings.File) },
            { new StringContent(passphrase), "passphrase" }
        };

        HttpResponseMessage response;
        try
        {
            response = await CliRender.StatusAsync(
                "Staging environment restore...", settings,
                () => client.PostAsync(ApiRoutes.Environment.Restore, content, cancellationToken)).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            throw new CliExitException(
                1,
                "The restore outcome is uncertain because the host connection closed. Reconnect and inspect the environment before retrying.");
        }

        using (response)
        {
            await CliHttpErrors.EnsureSuccessAsync(response, "Restoring the environment", cancellationToken).ConfigureAwait(false);
        }

        const string message = "Restore staged. The host is restarting.";
        if (!settings.Json && !settings.Quiet &&
            AnsiConsole.Profile.Capabilities.Interactive && AnsiConsole.Profile.Capabilities.Ansi)
        {
            AnsiConsole.Write(CliRender.Card("Environment restore", new Text(message, CliTheme.Value), CliTheme.Success));
        }
        else
        {
            CliRender.Success(message);
        }

        return 0;
    }
}

public sealed class EnvironmentRecoverSettings : OscovaCommandSettings
{
    [CommandOption("--yes")]
    [Description("Recover without asking for confirmation.")]
    public bool Yes { get; init; }
}

/// <summary>
/// Restores the previous environment after an interrupted activation.
/// </summary>
public sealed class EnvironmentRecoverCommand(CliPaths paths) : AsyncCommand<EnvironmentRecoverSettings>
{
    public override async Task<int> ExecuteAsync(
        CommandContext context,
        EnvironmentRecoverSettings settings,
        CancellationToken cancellationToken)
    {
        CliOutput.Configure(settings);
        CliRender.ConfirmDestructive(
            settings,
            settings.Yes,
            "Oscova will inspect retained recovery evidence and preserve the correct environment.");
        cancellationToken.ThrowIfCancellationRequested();

        using var lifecycleLease = await LocalLifecycleLease.AcquireAsync(paths.RootDirectory, cancellationToken).ConfigureAwait(false);
        using var updateLease = ApplicationUpdateFiles.AcquireUpdateLease();
        using var lease = EnvironmentRestoreBootstrapper.AcquireEnvironmentLease();
        var activity = new CliActivity(
            !settings.Json && !settings.Quiet &&
            AnsiConsole.Profile.Capabilities.Interactive && AnsiConsole.Profile.Capabilities.Ansi,
            "Recovering environment...");
        RecoveryOutcome outcome;
        try
        {
            var pending = EnvironmentRestoreBootstrapper.ReadPendingOperationOrNull();
            EnvironmentRestoreBootstrapper.RecoverPendingOperation();
            outcome = pending?.Mode == EnvironmentOperationMode.Reset
                ? RecoveryOutcome.PreviousResetEnvironmentRestored
                : RecoveryOutcome.PreviousEnvironmentRestored;
        }
        finally
        {
            await activity.StopAsync().ConfigureAwait(false);
        }

        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(new { completed = true, outcome = ToJsonValue(outcome) }, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var message = outcome switch
            {
                RecoveryOutcome.PreviousResetEnvironmentRestored =>
                    "Recovered the environment retained before reset. Review and archive _pending recovery evidence before another environment operation.",
                _ => "Recovered the previous environment. Review and archive _pending recovery evidence before importing again."
            };
            if (!settings.Quiet && AnsiConsole.Profile.Capabilities.Interactive && AnsiConsole.Profile.Capabilities.Ansi)
            {
                AnsiConsole.Write(CliRender.Card("Environment recovery", new Text(message, CliTheme.Value), CliTheme.Success));
            }
            else
            {
                CliRender.Success(message);
            }
        }

        return 0;
    }

    private static string ToJsonValue(RecoveryOutcome outcome) => outcome switch
    {
        RecoveryOutcome.PreviousResetEnvironmentRestored => "previous-reset-environment-restored",
        _ => "previous-environment-restored"
    };

    private enum RecoveryOutcome
    {
        PreviousEnvironmentRestored,
        PreviousResetEnvironmentRestored
    }
}

internal static class EnvironmentCommandSupport
{
    /// <summary>
    /// Reads the passphrase from the option or prompts for it, never echoing and never persisting it.
    /// </summary>
    public static string ResolvePassphrase(OscovaCommandSettings settings, string? provided)
    {
        if (!string.IsNullOrWhiteSpace(provided))
        {
            return provided;
        }

        if (settings.NoInteractive || settings.Json || !AnsiConsole.Profile.Capabilities.Interactive)
        {
            throw new CliExitException(4, "--passphrase is required when the CLI cannot prompt.");
        }

        return AnsiConsole.Prompt(new TextPrompt<string>("Passphrase").Secret());
    }
}
