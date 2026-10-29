using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Credentials;
using Oscova.Agents.Llm;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Oscova.Agents.Infrastructure.Chat;

/// <summary>
/// OpenRouter model provider using OpenAI-compatible chat completions and model listing.
/// </summary>
[CredentialSlot(Key = "openrouter", Label = "OpenRouter API Key", Description = "API key used by OpenRouter model providers.")]
public sealed class OpenRouterModelProvider(
    ILogger<OpenRouterModelProvider> logger,
    IHttpClientFactory httpClientFactory) : OpenAiCompatibleModelProviderBase(logger, httpClientFactory)
{
    private static readonly Uri DefaultEndpointUriValue = new("https://openrouter.ai/api/v1/");

    public override string Type => ModelProviderKeys.OpenRouter;

    public override string? DefaultModel => "openai/gpt-4o-mini";

    protected override Uri? DefaultEndpointUri => DefaultEndpointUriValue;

    protected override async Task<IReadOnlyList<string>> GetAvailableModelIdsAsync(
        ModelProviderConfig config,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        return await GetOpenRouterModelsAsync(config, apiKey, cancellationToken).ConfigureAwait(false);
    }

    protected override IChatClient WrapChatClient(IChatClient client, ModelProviderConfig config)
    {
        return config.SupportsThinking ? new ReasoningRecoveryChatClient(client) : client;
    }

    private async Task<IReadOnlyList<string>> GetOpenRouterModelsAsync(
        ModelProviderConfig config,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        Uri? endpoint = null;
        try
        {
            endpoint = ResolveEndpoint(config);
            var modelsEndpoint = new Uri(EnsureTrailingSlash(endpoint), "models");
            using var request = new HttpRequestMessage(HttpMethod.Get, modelsEndpoint);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            var httpClient = HttpClientFactory.CreateClient();
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var modelIds = new List<string>();
            foreach (var model in data.EnumerateArray())
            {
                if (model.TryGetProperty("id", out var idElement)
                    && idElement.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(idElement.GetString()))
                {
                    modelIds.Add(idElement.GetString()!);
                }
            }

            return NormalizeModelIds(modelIds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to fetch OpenRouter models from {Endpoint}", endpoint);
            return [];
        }
    }
}
