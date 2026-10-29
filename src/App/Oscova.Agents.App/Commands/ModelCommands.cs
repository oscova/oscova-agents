using System.ComponentModel;
using System.Net.Http.Json;
using System.Diagnostics;
using Oscova.Agents.App.Cli;
using Oscova.Agents.Core.ApiContracts.ModelProviders;
using Oscova.Agents.Host.Constants;
using Oscova.Agents.Llm;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Oscova.Agents.App.Commands;

public sealed class ModelProviderIdSettings : OscovaCommandSettings
{
    [CommandArgument(0, "<PROVIDER_ID>")]
    [Description("Model provider identifier. List valid ids with 'oscova models list'.")]
    public string ProviderId { get; init; } = string.Empty;
}

/// <summary>
/// Checks that a configured model provider answers.
/// </summary>
public sealed class ModelsTestCommand(CliHostResolver resolver, HostApiClient apiClient) : HostCommandBase<ModelProviderIdSettings>(resolver, apiClient)
{
    protected override async Task<int> RunAsync(HttpClient client, ModelProviderIdSettings settings, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        var providerName = settings.ProviderId;
        ProviderTestResult result;
        try
        {
            var provider = await ModelCommandSupport.FindProviderAsync(client, settings.ProviderId, cancellationToken).ConfigureAwait(false);
            providerName = provider.Name;
            result = await CliRender.StatusAsync(
                $"Testing {provider.Name}...", settings,
                () => PostAsync(client, provider, cancellationToken)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var cancelled = cancellationToken.IsCancellationRequested;
            result = new()
            {
                Outcome = cancelled ? "cancelled" : "timedout", Reason = cancelled ? "request-cancelled" : "request-timeout",
                TestKind = "transport", ElapsedMilliseconds = elapsed.ElapsedMilliseconds, CleanupConfirmed = false,
                Message = cancelled ? "The request was cancelled. Host cleanup could not be confirmed."
                    : "The host did not return a test result before the request timed out. Host cleanup could not be confirmed."
            };
        }
        catch (HttpRequestException exception) when (exception.StatusCode is null)
        {
            result = new()
            {
                Outcome = "failed", Reason = "request-failed", TestKind = "transport",
                ElapsedMilliseconds = elapsed.ElapsedMilliseconds, CleanupConfirmed = false,
                Message = $"The request failed: {exception.Message} Host cleanup could not be confirmed."
            };
        }

        if (settings.Json)
        {
            // The cancellation outcome must survive the token that interrupted its request.
            await CliOutput.WriteJsonAsync(result, CancellationToken.None).ConfigureAwait(false);
            return ExitCode(result);
        }

        if (!settings.Quiet)
        {
            var rows = new List<(string Key, string Value)>
            {
                ("Outcome", result.Outcome), ("Elapsed", $"{result.ElapsedMilliseconds} ms"),
                ("Cleanup", result.CleanupConfirmed ? "confirmed" : "unconfirmed")
            };
            if (result.LoadMilliseconds is { } load) { rows.Add(("Load", $"{load} ms")); }

            if (result.InferenceMilliseconds is { } inference) { rows.Add(("Inference", $"{inference} ms")); }

            if (result.Runtime is { } runtime)
            {
                rows.Add(("Backend", runtime.Backend ?? "unverified"));
                if (runtime.Device is { } device) { rows.Add(("Device", device)); }

                if (runtime.OffloadedLayers is { } layers) { rows.Add(("Offloaded layers", layers.ToString())); }
            }

            CliRender.KeyValue("Model provider test", rows);
        }

        // The endpoint answers 200 whether or not the provider works, so the payload decides the exit code.
        if (!result.Success)
        {
            CliRender.Failure(string.IsNullOrWhiteSpace(result.Message) ? $"{providerName} did not answer." : result.Message);
            return ExitCode(result);
        }

        var message = string.IsNullOrWhiteSpace(result.Message) ? $"{providerName} answered." : result.Message;
        CliRender.Success(message);

        return 0;
    }

    private static async Task<ProviderTestResult> PostAsync(HttpClient client, ModelProviderConfig provider, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(75));
        using var response = await client.PostAsJsonAsync(ApiRoutes.ModelProviders.Test, provider, CliJson.Options, timeout.Token).ConfigureAwait(false);
        await CliHttpErrors.EnsureSuccessAsync(response, "Testing model provider", cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ProviderTestResult>(CliJson.Options, timeout.Token).ConfigureAwait(false)
            ?? throw new CliExitException(1, "The host returned an empty test result.");
    }

    private static int ExitCode(ProviderTestResult result) => result.Outcome switch
    {
        "passed" => 0, "skipped" => 2, "timedout" => 3, "cancelled" => 130, _ => 1
    };
}

/// <summary>
/// Lists the GGUF model files installed on the host.
/// </summary>
public sealed class ModelsLocalCommand(CliHostResolver resolver, HostApiClient apiClient) : HostCommandBase<OscovaCommandSettings>(resolver, apiClient)
{
    protected override async Task<int> RunAsync(HttpClient client, OscovaCommandSettings settings, CancellationToken cancellationToken)
    {
        var models = await client.GetFromJsonAsync<List<string>>(ApiRoutes.ModelProviders.LocalModels, CliJson.Options, cancellationToken).ConfigureAwait(false) ?? [];

        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(models, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        if (models.Count == 0)
        {
            AnsiConsole.Write(CliRender.Card(
                "Local models (0)",
                new Text("No local model files are installed.", CliTheme.Label),
                CliTheme.AccentDim));
        }
        else
        {
            var table = CliRender.Table("File");
            foreach (var model in models)
            {
                table.AddRow(CliRender.Value(model));
            }

            CliRender.Separator($"Local models ({models.Count})");
            AnsiConsole.Write(table);
        }

        return 0;
    }
}

/// <summary>
/// Lists the models a configured provider offers.
/// </summary>
public sealed class ModelsAvailableCommand(CliHostResolver resolver, HostApiClient apiClient) : HostCommandBase<ModelProviderIdSettings>(resolver, apiClient)
{
    protected override async Task<int> RunAsync(HttpClient client, ModelProviderIdSettings settings, CancellationToken cancellationToken)
    {
        var provider = await ModelCommandSupport.FindProviderAsync(client, settings.ProviderId, cancellationToken).ConfigureAwait(false);

        using var response = await CliRender.StatusAsync(
            $"Reading models from {provider.Name}...", settings,
            () => client.PostAsJsonAsync(
                ApiRoutes.ModelProviders.AvailableModels,
                provider,
                CliJson.Options,
                cancellationToken)).ConfigureAwait(false);
        await CliHttpErrors.EnsureSuccessAsync(response, "Listing available models", cancellationToken).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<AvailableModelsResult>(CliJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new CliExitException(1, "The host returned an empty model list.");

        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(result, cancellationToken).ConfigureAwait(false);
            return result.Success ? 0 : 1;
        }

        if (!result.Success)
        {
            CliRender.Failure(string.IsNullOrWhiteSpace(result.Message) ? $"{provider.Name} did not return a model list." : result.Message);
            return 1;
        }

        if (result.Models.Count == 0)
        {
            AnsiConsole.Write(CliRender.Card(
                $"{provider.Name} models (0)",
                new Text("The provider returned no models.", CliTheme.Label),
                CliTheme.AccentDim));
        }
        else
        {
            var table = CliRender.Table("Model", "Context", "Images", "Thinking");
            foreach (var model in result.Models)
            {
                table.AddRow(
                    CliRender.Value(model.Id),
                    CliRender.Value(model.ContextWindowSize?.ToString() ?? "-"),
                    CliRender.Value(Format(model.SupportsImages)),
                    CliRender.Value(Format(model.SupportsThinking)));
            }

            CliRender.Separator($"{provider.Name} models ({result.Models.Count})");
            AnsiConsole.Write(table);
        }

        return 0;
    }

    private static string Format(bool? value) => value is null ? "-" : value.Value ? "yes" : "no";
}

internal static class ModelCommandSupport
{
    /// <summary>
    /// Resolves a provider identifier to the stored configuration, which is what both write endpoints expect as a body.
    /// </summary>
    public static async Task<ModelProviderConfig> FindProviderAsync(HttpClient client, string providerId, CancellationToken cancellationToken)
    {
        var configuration = await client.GetFromJsonAsync<ModelProviderConfiguration>(ApiRoutes.ModelProviders.Configuration, CliJson.Options, cancellationToken).ConfigureAwait(false)
            ?? new ModelProviderConfiguration();

        return configuration.ModelProviders.FirstOrDefault(provider =>
                   string.Equals(provider.Id.ToString(), providerId, StringComparison.OrdinalIgnoreCase))
               ?? throw new CliExitException(4, $"No model provider '{providerId}'. List providers with 'oscova models list'.");
    }
}
