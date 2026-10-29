using System.ComponentModel;
using System.Net.Http.Json;
using System.Text.Json;
using Oscova.Agents.App.Cli;
using Oscova.Agents.Core.ApiContracts.Environment;
using Oscova.Agents.Host.Constants;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Oscova.Agents.App.Commands;

/// <summary>Controls confirmation for the environment reset command.</summary>
public sealed class EnvironmentResetSettings : OscovaCommandSettings
{
    [CommandOption("--yes")]
    [Description("Confirm environment reset without prompting.")]
    public bool Yes { get; init; }
}

/// <summary>Restarts the selected Host with fresh app data while retaining the previous environment.</summary>
public sealed class EnvironmentResetCommand(
    CliHostResolver resolver,
    HostApiClient apiClient,
    CliAuthFileStore authStore) : HostCommandBase<EnvironmentResetSettings>(resolver, apiClient)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollTimeout = TimeSpan.FromMinutes(10);

    protected override async Task<int> RunAsync(
        HttpClient client,
        EnvironmentResetSettings settings,
        CancellationToken cancellationToken)
    {
        using var previewResponse = await client.GetAsync(ApiRoutes.Environment.Reset, cancellationToken).ConfigureAwait(false);
        await CliHttpErrors.EnsureSuccessAsync(previewResponse, "Checking environment reset", cancellationToken).ConfigureAwait(false);
        var preview = await previewResponse.Content
            .ReadFromJsonAsync<EnvironmentResetPreview>(CliJson.Options, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CliExitException(1, "The Host returned an empty environment reset preview.");

        if (!settings.Json && !settings.Quiet)
        {
            const string moved =
                "Users, conversations, settings, files, credentials, plugins, and keys will move to a retained sibling directory.";
            const string kept =
                "Models, voices, logs, CLI files, the installed tool, and Avatar files stay in place.";
            var warning =
                "Finish important work, run 'oscova avatar mcp stop', and stop external writers before continuing. " +
                "The current address stays available for setup, but reset network settings apply on later restarts.";
            CliRender.KeyValue("Environment reset",
            [
                ("Host", client.BaseAddress?.ToString() ?? "-"),
                ("Application data", preview.AppDataPath)
            ]);
            var changes = new Grid()
                .AddColumn(new GridColumn().NoWrap().PadRight(2))
                .AddColumn()
                .AddRow(new Text("Moved", CliTheme.Label), new Text(moved))
                .AddRow(new Text("Kept", CliTheme.Label), new Text(kept));
            AnsiConsole.Write(CliRender.Card("What changes", changes, CliTheme.Warning));
            AnsiConsole.Write(CliRender.Card("Before you continue", new Text(warning), CliTheme.Warning));
        }

        CliRender.ConfirmDestructive(
            settings,
            settings.Yes,
            "Everyone will be signed out and the selected Host will restart with a fresh environment.");

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync(
                ApiRoutes.Environment.Reset,
                new ResetEnvironmentRequest("RESET"),
                CliJson.Options,
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new CliExitException(
                1,
                $"The reset outcome is uncertain because the Host connection closed: {ex.Message}");
        }

        EnvironmentResetAccepted accepted;
        using (response)
        {
            await CliHttpErrors.EnsureSuccessAsync(response, "Resetting the environment", cancellationToken).ConfigureAwait(false);
            accepted = await response.Content
                .ReadFromJsonAsync<EnvironmentResetAccepted>(CliJson.Options, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new CliExitException(1, "The Host returned an empty environment reset response.");
        }

        var setupUrl = client.BaseAddress?.ToString() ?? "the selected Host address";
        await authStore.RemoveAsync(setupUrl, cancellationToken).ConfigureAwait(false);
        client.DefaultRequestHeaders.Authorization = null;

        if (!settings.Json && !settings.Quiet)
        {
            var retained = $"Previous environment retained at {accepted.BackupPath}.";
            const string restarting = "Oscova is restarting and reinstalling default plugins.";
            var acceptedDetails = new Grid()
                .AddColumn()
                .AddRow(new Text(retained))
                .AddRow(new Text(restarting, new Style(CliTheme.Warning)));
            AnsiConsole.Write(CliRender.Card("Reset accepted", acceptedDetails, CliTheme.Warning));
        }

        var completed = await CliRender.StatusAsync(
            "Waiting for Oscova to restart...",
            settings,
            () => WaitForCompletionAsync(client, accepted.OperationId, cancellationToken)).ConfigureAwait(false);
        if (!completed)
        {
            throw new CliExitException(
                1,
                "Reset was accepted, but completion could not be verified within ten minutes. " +
                $"Reopen {setupUrl}. Previous data remains at {accepted.BackupPath}. " +
                "If startup stopped, use 'oscova environment recover --yes' with the same OSCOVA_APP_DATA_DIR.");
        }

        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(
                new { completed = true, operationId = accepted.OperationId, backupPath = accepted.BackupPath, setupUrl },
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var message = $"Environment reset completed. Open {setupUrl} to create the operator account.";
            if (settings.Quiet)
            {
                CliRender.Success(message);
            }
            else
            {
                AnsiConsole.Write(CliRender.Card(
                    "Environment ready",
                    new Text(message, new Style(CliTheme.Success)),
                    CliTheme.Success));
            }
        }

        return 0;
    }

    private static async Task<bool> WaitForCompletionAsync(
        HttpClient client,
        string operationId,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + PollTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(PollInterval);
            try
            {
                using var response = await client
                    .GetAsync(ApiRoutes.Environment.ResetCompletion(operationId), requestTimeout.Token)
                    .ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var completion = await response.Content
                        .ReadFromJsonAsync<EnvironmentResetCompletion>(CliJson.Options, requestTimeout.Token)
                        .ConfigureAwait(false);
                    if (completion?.Completed == true)
                    {
                        return true;
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch (HttpRequestException)
            {
            }
            catch (JsonException)
            {
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(
                    remaining < PollInterval ? remaining : PollInterval,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }
}
