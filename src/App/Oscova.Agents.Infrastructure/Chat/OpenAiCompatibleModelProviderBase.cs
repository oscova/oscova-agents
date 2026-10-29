using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Models;
using Oscova.Agents.Llm;
using System.ClientModel;

namespace Oscova.Agents.Infrastructure.Chat;

/// <summary>
/// Shared OpenAI-compatible provider implementation for hosted and custom chat-completions endpoints.
/// </summary>
public abstract class OpenAiCompatibleModelProviderBase(
    ILogger logger,
    IHttpClientFactory httpClientFactory) : IModelProvider
{
    private const string DefaultModelId = "gpt-4o-mini";
    private const string PlaceholderApiKey = "not-needed";

    protected ILogger Logger { get; } = logger;

    protected IHttpClientFactory HttpClientFactory { get; } = httpClientFactory;

    public abstract string Type { get; }

    public string? DefaultEndpoint => DefaultEndpointUri?.ToString();

    public virtual string? DefaultModel => DefaultModelId;

    public bool? DefaultIsLocalEndpoint => null;

    public virtual bool SupportsEmbedding => true;

    protected virtual Uri? DefaultEndpointUri => null;

    protected virtual bool RequiresApiKey => true;

    public virtual ValueTask<IChatClient> CreateClientAsync(
        ModelProviderConfig config,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        var model = ResolveModelId(config);
        var apiCredential = new ApiKeyCredential(ResolveApiKeyOrThrow(apiKey));
        var inner = BuildChatCompletionsClient(model, apiCredential, config);

        var configured = inner.AsBuilder()
            .ConfigureOptions(options => ApplyRequestDefaults(options, config))
            .Build();
        return ValueTask.FromResult(WrapChatClient(configured, config));
    }

    public virtual ValueTask<IEmbeddingGenerator<string, Embedding<float>>> CreateEmbeddingGeneratorAsync(
        ModelProviderConfig config,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        var model = string.IsNullOrWhiteSpace(config.ModelId) ? "text-embedding-3-small" : config.ModelId!;
        var resolvedApiKey = ResolveApiKeyOrThrow(apiKey);

        var clientOptions = BuildClientOptions(config);
        var credential = new ApiKeyCredential(resolvedApiKey);
        var openAiClient = new OpenAIClient(credential, clientOptions);
        var embeddingClient = openAiClient.GetEmbeddingClient(model);
        return ValueTask.FromResult(embeddingClient.AsIEmbeddingGenerator());
    }

    public virtual async Task<IReadOnlyList<ModelDescriptor>> GetAvailableModelsAsync(
        ModelProviderConfig config,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        var modelIds = await GetAvailableModelIdsAsync(config, apiKey, cancellationToken).ConfigureAwait(false);
        return CreateModelDescriptors(modelIds);
    }

    public async Task<(bool Success, string Message)> TestAsync(
        ModelProviderConfig config,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey) && RequiresApiKey)
        {
            return (false, BuildMissingApiKeyMessage());
        }

        return await ExecuteHealthCheckAsync(config, apiKey, cancellationToken).ConfigureAwait(false);
    }

    protected virtual IChatClient WrapChatClient(IChatClient client, ModelProviderConfig config)
    {
        return client;
    }

    protected virtual void ApplyRequestDefaults(ChatOptions options, ModelProviderConfig config)
    {
        if (config.SupportsThinking)
        {
            options.Reasoning ??= new ReasoningOptions
            {
                Effort = ReasoningEffort.Medium,
                Output = ReasoningOutput.Full
            };
        }
    }

    private async Task<(bool Success, string Message)> ExecuteHealthCheckAsync(
        ModelProviderConfig config,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        try
        {
            var options = BuildClientOptions(config);
            var credential = new ApiKeyCredential(ResolveApiKeyOrThrow(apiKey));
            var openAiClient = new OpenAIClient(credential, options);
            var modelClient = openAiClient.GetOpenAIModelClient();
            ClientResult<OpenAIModelCollection> result = await modelClient.GetModelsAsync(cancellationToken).ConfigureAwait(false);

            return result.Value.Any()
                ? (true, "Successfully connected to the OpenAI API.")
                : (false, "Connected to the OpenAI API, but no models were returned.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to contact OpenAI endpoint {Endpoint}", config.BaseUrl);
            return (false, ex.Message);
        }
    }

    protected virtual async Task<IReadOnlyList<string>> GetAvailableModelIdsAsync(
        ModelProviderConfig config,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        var resolvedApiKey = ResolveApiKeyOrThrow(apiKey);

        try
        {
            var options = BuildClientOptions(config);
            var credential = new ApiKeyCredential(resolvedApiKey);
            var openAiClient = new OpenAIClient(credential, options);
            var modelClient = openAiClient.GetOpenAIModelClient();
            ClientResult<OpenAIModelCollection> result = await modelClient.GetModelsAsync(cancellationToken).ConfigureAwait(false);

            return NormalizeModelIds(result.Value.Select(model => model.Id));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to fetch OpenAI models from {Endpoint}", config.BaseUrl);
            return [];
        }
    }

    protected IChatClient BuildChatCompletionsClient(string model, ApiKeyCredential credential, ModelProviderConfig config)
    {
        var chatClient = new ChatClient(model, credential, BuildClientOptions(config));
        return chatClient.AsIChatClient();
    }

    protected OpenAIClientOptions BuildClientOptions(ModelProviderConfig config)
    {
        if (TryResolveEndpoint(config, out var uri))
        {
            return new OpenAIClientOptions
            {
                Endpoint = uri
            };
        }

        return new OpenAIClientOptions();
    }

    protected bool TryResolveEndpoint(ModelProviderConfig config, out Uri? endpoint)
    {
        if (!string.IsNullOrWhiteSpace(config.BaseUrl)
            && Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out var configuredEndpoint))
        {
            endpoint = configuredEndpoint;
            return true;
        }

        if (DefaultEndpointUri is not null)
        {
            endpoint = DefaultEndpointUri;
            return true;
        }

        endpoint = null;
        return false;
    }

    protected Uri ResolveEndpoint(ModelProviderConfig config)
    {
        if (TryResolveEndpoint(config, out var endpoint) && endpoint is not null)
        {
            return endpoint;
        }

        throw new InvalidOperationException($"A base URL is required for {Type}.");
    }

    protected string ResolveModelId(ModelProviderConfig config)
    {
        return string.IsNullOrWhiteSpace(config.ModelId)
            ? DefaultModel ?? DefaultModelId
            : config.ModelId!;
    }

    protected string ResolveApiKeyOrThrow(string? apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            return apiKey;
        }

        if (!RequiresApiKey)
        {
            return PlaceholderApiKey;
        }

        throw new InvalidOperationException(BuildMissingApiKeyMessage());
    }

    protected string BuildMissingApiKeyMessage()
    {
        return $"An API key is required for {Type}. Link a saved credential, then try again.";
    }

    protected static IReadOnlyList<ModelDescriptor> CreateModelDescriptors(IEnumerable<string> modelIds)
    {
        return [.. modelIds.Select(id => new ModelDescriptor { Id = id })];
    }

    protected static IReadOnlyList<string> NormalizeModelIds(IEnumerable<string?> modelIds)
    {
        return [.. modelIds
            .Where(modelId => !string.IsNullOrWhiteSpace(modelId))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(modelId => modelId, StringComparer.Ordinal)];
    }

    protected static Uri EnsureTrailingSlash(Uri uri)
    {
        var value = uri.ToString();
        return value.EndsWith("/", StringComparison.Ordinal) ? uri : new Uri(value + "/");
    }
}
