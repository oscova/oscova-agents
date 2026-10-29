using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Credentials;
using Oscova.Agents.Llm;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http.Headers;
using System.Text.Json;
using SdkChatClient = OpenAI.Chat.ChatClient;

namespace Oscova.Agents.Infrastructure.Chat;

/// <summary>Connects DeepSeek chat models through its OpenAI-compatible API.</summary>
[CredentialSlot(Key = "deepseek", Label = "DeepSeek API Key", Description = "API key used by DeepSeek model providers.")]
public sealed class DeepSeekModelProvider(
    ILogger<DeepSeekModelProvider> logger,
    IHttpClientFactory httpClientFactory) : OpenAiCompatibleModelProviderBase(logger, httpClientFactory)
{
    private static readonly Uri DefaultEndpointUriValue = new("https://api.deepseek.com/");

    public override string Type => ModelProviderKeys.DeepSeek;

    public override string? DefaultModel => "deepseek-flash";

    public override bool SupportsEmbedding => false;

    protected override Uri? DefaultEndpointUri => DefaultEndpointUriValue;

    /// <inheritdoc />
    public override ValueTask<IChatClient> CreateClientAsync(
        ModelProviderConfig config,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        var model = ResolveModelId(config);
        var credential = new ApiKeyCredential(ResolveApiKeyOrThrow(apiKey));
        IChatClient CreateClient(IReadOnlyList<ChatMessage>? history = null)
        {
            var options = BuildClientOptions(config);
            if (history is not null)
            {
                // A call-local policy prevents concurrent requests from sharing reasoning history.
                options.AddPolicy(new DeepSeekReasoningChatClient.RequestPolicy(history, config.SupportsThinking), PipelinePosition.PerCall);
            }

            return new SdkChatClient(model, credential, options).AsIChatClient().AsBuilder()
                .ConfigureOptions(chatOptions => ApplyRequestDefaults(chatOptions, config)).Build();
        }

        return ValueTask.FromResult<IChatClient>(new DeepSeekReasoningChatClient(CreateClient(), history => CreateClient(history)));
    }

    /// <inheritdoc />
    public override ValueTask<IEmbeddingGenerator<string, Embedding<float>>> CreateEmbeddingGeneratorAsync(
        ModelProviderConfig config,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("DeepSeek embedding generation is not enabled for this provider.");
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<ModelDescriptor>> GetAvailableModelsAsync(
        ModelProviderConfig config,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        var credential = ResolveApiKeyOrThrow(apiKey);
        var endpoint = new Uri(EnsureTrailingSlash(ResolveEndpoint(config)), "models");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            using var response = await HttpClientFactory.CreateClient()
                .SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var models = new List<ModelDescriptor>();
            foreach (var model in data.EnumerateArray())
            {
                if (!model.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(id.GetString()))
                {
                    continue;
                }

                // Existing model selection applies these fields to the provider configuration.
                models.Add(new ModelDescriptor
                {
                    Id = id.GetString()!,
                    ContextWindowSize = model.TryGetProperty("context_window", out var context) && context.ValueKind == JsonValueKind.Number
                        && context.TryGetInt32(out var size) ? size : null,
                    SupportsImages = model.TryGetProperty("input_modalities", out var modalities) && modalities.ValueKind == JsonValueKind.Array
                        ? modalities.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == "image") : null,
                    SupportsThinking = model.TryGetProperty("effort", out var effort) && effort.ValueKind == JsonValueKind.Object
                        && effort.TryGetProperty("supported_levels", out var levels) && levels.ValueKind == JsonValueKind.Array
                        ? levels.GetArrayLength() > 0 : null
                });
            }

            return models;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to fetch DeepSeek models from {Endpoint}", endpoint);
            return [];
        }
    }
}
