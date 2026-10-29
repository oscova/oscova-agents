using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OllamaSharp;
using OllamaSharp.Models;
using Oscova.Agents.Llm;

namespace Oscova.Agents.Infrastructure.Chat;

public sealed class OllamaModelProvider(ILogger<OllamaModelProvider> logger) : IModelProvider, IModelDownloadProvider
{
    private readonly ILogger<OllamaModelProvider> _logger = logger;

    private static readonly Uri DefaultEndpointUri = new("http://localhost:11434/");
    private const string DefaultModelId = "llama3.2";

    // Clients built per embedding call share one pool, because a new connection to an IPv4-only localhost on Windows can wait about 2 seconds for the IPv6 attempt.
    // Cookies stay off so one configured endpoint's cookies never reach another, and connections recycle so DNS changes are picked up.
    private static readonly SocketsHttpHandler SharedHandler = new()
    {
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    };

    public string Type => ModelProviderKeys.Ollama;

    public string? DefaultEndpoint => DefaultEndpointUri.ToString();

    public string? DefaultModel => DefaultModelId;

    public bool? DefaultIsLocalEndpoint => null;

    public bool SupportsEmbedding => true;

    public ValueTask<IChatClient> CreateClientAsync(
        ModelProviderConfig config,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        // Apply Ollama request defaults at the provider-created client so every run path uses them.
        IChatClient client = BuildClient(config);
        var configured = client.AsBuilder()
            .ConfigureOptions(options => ApplyRequestDefaults(options, config))
            .Build();
        return ValueTask.FromResult(configured);
    }

    private static void ApplyRequestDefaults(ChatOptions options, ModelProviderConfig config)
    {
        options.AdditionalProperties ??= [];

        // OllamaSharp maps these AdditionalProperties keys to Ollama request fields.
        if (config.ContextWindowSize > 0 && !options.AdditionalProperties.ContainsKey("num_ctx"))
        {
            options.AdditionalProperties["num_ctx"] = config.ContextWindowSize;
        }

        var keepAlive = config.OllamaKeepAlive?.Trim();
        if (!string.IsNullOrWhiteSpace(keepAlive)
            && !options.AdditionalProperties.ContainsKey("keep_alive"))
        {
            options.AdditionalProperties["keep_alive"] = keepAlive;
        }

        if (!options.AdditionalProperties.ContainsKey("think"))
        {
            options.AdditionalProperties["think"] = config.SupportsThinking;
        }
    }

    public ValueTask<IEmbeddingGenerator<string, Embedding<float>>> CreateEmbeddingGeneratorAsync(ModelProviderConfig config, string? apiKey = null, CancellationToken cancellationToken = default)
    {
        // OllamaApiClient implements IEmbeddingGenerator<string, Embedding<float>>
        var client = BuildClient(config);
        return ValueTask.FromResult<IEmbeddingGenerator<string, Embedding<float>>>(client);
    }

    public async Task<IReadOnlyList<ModelDescriptor>> GetAvailableModelsAsync(
        ModelProviderConfig config,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = BuildClient(config);
            var models = await client.ListLocalModelsAsync(cancellationToken).ConfigureAwait(false);

            var descriptors = new List<ModelDescriptor>();
            foreach (var model in models)
            {
                if (string.IsNullOrWhiteSpace(model.Name))
                {
                    continue;
                }

                descriptors.Add(await BuildModelDescriptorAsync(client, model.Name, model.Size, cancellationToken).ConfigureAwait(false));
            }

            return descriptors;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch Ollama models from {BaseUrl}", config.BaseUrl);
            return [];
        }
    }

    private async Task<ModelDescriptor> BuildModelDescriptorAsync(
        OllamaApiClient client,
        string modelName,
        long sizeBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            var details = await client.ShowModelAsync(
                new ShowModelRequest { Model = modelName, Verbose = true },
                cancellationToken).ConfigureAwait(false);
            return new ModelDescriptor
            {
                Id = modelName,
                ContextWindowSize = ReadArchitectureInt(details, "context_length") is > 0 and var contextLength
                    ? contextLength
                    : null,
                SupportsImages = HasCapability(details, "vision"),
                SupportsThinking = HasCapability(details, "thinking"),
                SizeBytes = sizeBytes,
                KvCacheBytesPerToken = ReadKvCacheBytesPerToken(details)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read Ollama model details for {Model}; returning id only.", modelName);
            return new ModelDescriptor { Id = modelName, SizeBytes = sizeBytes };
        }
    }

    /// <summary>Derives the per-token cache cost from the show response, or null when the metadata is incomplete.</summary>
    private static long? ReadKvCacheBytesPerToken(ShowModelResponse details)
    {
        var layers = ReadArchitectureInt(details, "block_count") ?? 0;
        var heads = ReadArchitectureInt(details, "attention.head_count") ?? 0;
        if (layers <= 0 || heads <= 0)
        {
            return null;
        }

        var embedding = ReadArchitectureInt(details, "embedding_length") ?? 0;
        var keyLength = ModelFitEstimator.HeadDimension(
            ReadArchitectureInt(details, "attention.key_length"),
            embedding,
            heads);
        var valueLength = ModelFitEstimator.HeadDimension(
            ReadArchitectureInt(details, "attention.value_length"),
            embedding,
            heads);
        if (keyLength is null || valueLength is null)
        {
            return null;
        }

        var kvHeadLayerTotal = ReadKvHeadLayerTotal(details, layers);
        return kvHeadLayerTotal is { } total
            ? ModelFitEstimator.KvCacheBytesPerToken(total, keyLength.Value, valueLength.Value)
            : null;
    }

    /// <summary>Reads either a uniform KV-head count or one count per hybrid-model layer.</summary>
    private static long? ReadKvHeadLayerTotal(ShowModelResponse details, int layerCount)
    {
        var architecture = details.Info?.Architecture;
        if (string.IsNullOrWhiteSpace(architecture)
            || details.Info?.ExtraInfo is null
            || !details.Info.ExtraInfo.TryGetValue($"{architecture}.attention.head_count_kv", out var value)
            || value is not JsonElement element)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var uniformHeads) && uniformHeads > 0)
        {
            return checked((long)layerCount * uniformHeads);
        }

        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != layerCount)
        {
            return null;
        }

        long total = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var heads) || heads < 0)
            {
                return null;
            }

            total = checked(total + heads);
        }

        return total > 0 ? total : null;
    }

    /// <summary>Reads an architecture-scoped integer from the show response metadata block.</summary>
    private static int? ReadArchitectureInt(ShowModelResponse details, string suffix)
    {
        var architecture = details.Info?.Architecture;
        if (string.IsNullOrWhiteSpace(architecture) || details.Info?.ExtraInfo is null)
        {
            return null;
        }

        if (!details.Info.ExtraInfo.TryGetValue($"{architecture}.{suffix}", out var value))
        {
            return null;
        }

        return value is JsonElement element
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out var result)
            && result > 0
                ? result
                : 0;
    }

    private static bool? HasCapability(ShowModelResponse details, string capability)
    {
        if (details.Capabilities is null)
        {
            return null;
        }

        return details.Capabilities.Any(c => string.Equals(c, capability, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<(bool Success, string Message)> TestAsync(
        ModelProviderConfig config,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = BuildClient(config);
            var isRunning = await client.IsRunningAsync(cancellationToken).ConfigureAwait(false);
            if (!isRunning)
            {
                return (false, "Ollama is not running at the configured endpoint.");
            }

            var effectiveModelId = string.IsNullOrWhiteSpace(config.ModelId) ? DefaultModelId : config.ModelId!;
            var models = await client.ListLocalModelsAsync(cancellationToken).ConfigureAwait(false);
            var isModelAvailable = models.Any(m => m.Name == effectiveModelId);

            return isModelAvailable
                ? (true, "Connected to Ollama successfully.")
                : (false, $"Ollama is running but model '{effectiveModelId}' is not available locally.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ollama health check failed for {BaseUrl}", config.BaseUrl);
            return (false, ex.Message);
        }
    }

    /// <summary>Pulls the requested model into the configured Ollama server's cache.</summary>
    public async Task DownloadModelAsync(
        ModelProviderConfig config,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(config.ModelId))
        {
            throw new ArgumentException("An Ollama model ID is required.", nameof(config));
        }

        if (!string.IsNullOrWhiteSpace(config.BaseUrl)
            && (TryCreateUri(config.BaseUrl) is not { } endpoint
                || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
                || string.IsNullOrWhiteSpace(endpoint.Host)))
        {
            throw new ArgumentException("The Ollama base URL must be an absolute HTTP or HTTPS URL.", nameof(config));
        }

        // The server owns the model cache, including pulls for models absent from its installed list.
        using var httpClient = new HttpClient(new PullCancellationHandler(cancellationToken));
        using var client = BuildClient(config, httpClient);
        var succeeded = false;
        try
        {
            await foreach (var response in client.PullModelAsync(config.ModelId.Trim(), cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (response is null) { continue; }
                // Percent covers the current layer, while preparation stages have no byte total.
                progress?.Report(new ModelDownloadProgress
                {
                    Percent = response.Total > 0 ? response.Percent : null,
                    Status = response.Status
                });
                succeeded |= string.Equals(response.Status, "success", StringComparison.Ordinal);
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!succeeded)
        {
            throw new InvalidOperationException("Ollama ended the model pull without confirming success. Try downloading the model again.");
        }
    }

    /// <summary>Closes the pull response when cancellation interrupts an otherwise uncancellable line read.</summary>
    private sealed class PullCancellationHandler(CancellationToken cancellationToken) : DelegatingHandler(new HttpClientHandler())
    {
        private CancellationTokenRegistration _responseCancellation;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await base.SendAsync(request, token).ConfigureAwait(false);
            // The streamed reader does not pass its token to pending reads, so close the response body.
            _responseCancellation = cancellationToken.Register(response.Dispose);
            return response;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _responseCancellation.Dispose(); }

            base.Dispose(disposing);
        }
    }

    private static OllamaApiClient BuildClient(ModelProviderConfig config, HttpClient? httpClient = null)
    {
        var uri = TryCreateUri(config.BaseUrl) ?? DefaultEndpointUri;
        var model = string.IsNullOrWhiteSpace(config.ModelId) ? DefaultModelId : config.ModelId!;
        httpClient ??= new HttpClient(SharedHandler, disposeHandler: false);
        httpClient.BaseAddress = uri;
        return new OllamaApiClient(httpClient, model);
    }

    private static Uri? TryCreateUri(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        if (!baseUrl.EndsWith('/'))
        {
            baseUrl += "/";
        }

        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri : null;
    }
}

