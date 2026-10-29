using Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Credentials;
using Oscova.Agents.Llm;

namespace Oscova.Agents.Infrastructure.Chat;

[CredentialSlot(Key = "anthropic", Label = "Anthropic API Key", Description = "API key used by Anthropic model providers.")]
public sealed class AnthropicModelProvider(ILogger<AnthropicModelProvider> logger) : IModelProvider
{
    private readonly ILogger<AnthropicModelProvider> _logger = logger;

    private static readonly Uri DefaultEndpointUri = new("https://api.anthropic.com/");
    private const string DefaultModelId = ModelProviderConfigPresets.AnthropicDefaultModelId;

    public string Type => ModelProviderKeys.Anthropic;

    public string? DefaultEndpoint => DefaultEndpointUri.ToString();

    public string? DefaultModel => DefaultModelId;

    public bool? DefaultIsLocalEndpoint => null;

    public bool SupportsEmbedding => false;

    public ValueTask<IChatClient> CreateClientAsync(
        ModelProviderConfig config,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("API key is required for Anthropic.");
        }

        var client = BuildClient(config, apiKey);
        var modelId = ResolveModelId(config);
        var configured = client.AsIChatClient(modelId).AsBuilder()
            .ConfigureOptions(options =>
            {
                // Keep blank limits predictable instead of inheriting the adapter's 1024-token cap.
                options.MaxOutputTokens ??= config.ChatOptions.MaxOutputTokens ?? ModelProviderConfigPresets.AnthropicDefaultMaxOutputTokens;

                // The adapter requests summarized thinking through the shared reasoning contract.
                options.Reasoning ??= config.ChatOptions.Clone().Reasoning;
                if (config.SupportsThinking)
                {
                    options.Reasoning ??= new ReasoningOptions();
                    options.Reasoning.Output ??= ReasoningOutput.Full;
                }
            })
            .Build();
        return ValueTask.FromResult(configured);
    }

    public ValueTask<IEmbeddingGenerator<string, Embedding<float>>> CreateEmbeddingGeneratorAsync(
        ModelProviderConfig config,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Anthropic embedding generation is not enabled for this provider.");
    }

    public async Task<IReadOnlyList<ModelDescriptor>> GetAvailableModelsAsync(
        ModelProviderConfig config,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return [];
        }

        try
        {
            var client = BuildClient(config, apiKey);
            var modelListPage = await client.Models.List(cancellationToken: cancellationToken).ConfigureAwait(false);

            // Anthropic's listing exposes ids only; no context window or capability metadata.
            return [.. modelListPage.Items
                .Select(model => model.ID)
                .Where(modelId => !string.IsNullOrWhiteSpace(modelId))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(modelId => modelId, StringComparer.Ordinal)
                .Select(modelId => new ModelDescriptor { Id = modelId })];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch Anthropic models from {BaseUrl}", ResolveEndpoint(config));
            return [];
        }
    }

    public async Task<(bool Success, string Message)> TestAsync(
        ModelProviderConfig config,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return (false, "API key is required for Anthropic.");
        }

        try
        {
            var client = BuildClient(config, apiKey);
            var modelListPage = await client.Models.List(cancellationToken: cancellationToken).ConfigureAwait(false);

            return modelListPage.Items.Any()
                ? (true, "Connected to Anthropic successfully.")
                : (false, "Connected to Anthropic, but no models were returned.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Anthropic health check failed for {BaseUrl}", ResolveEndpoint(config));
            return (false, ex.Message);
        }
    }

    private static AnthropicClient BuildClient(ModelProviderConfig config, string apiKey)
    {
        if (!string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            return new AnthropicClient
            {
                ApiKey = apiKey,
                BaseUrl = ResolveEndpoint(config).ToString()
            };
        }

        return new AnthropicClient
        {
            ApiKey = apiKey
        };
    }

    private static Uri ResolveEndpoint(ModelProviderConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.BaseUrl)
            && Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out var uri))
        {
            return uri;
        }

        return DefaultEndpointUri;
    }

    private static string ResolveModelId(ModelProviderConfig config)
    {
        return string.IsNullOrWhiteSpace(config.ModelId) ? DefaultModelId : config.ModelId!;
    }

}
