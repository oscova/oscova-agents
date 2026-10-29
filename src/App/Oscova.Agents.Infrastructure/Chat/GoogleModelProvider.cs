using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Credentials;
using Oscova.Agents.Llm;

namespace Oscova.Agents.Infrastructure.Chat;

[CredentialSlot(Key = "google", Label = "Google API Key", Description = "API key used by Google Gemini model providers.")]
public sealed class GoogleModelProvider(ILogger<GoogleModelProvider> logger) : IModelProvider
{
    private readonly ILogger<GoogleModelProvider> _logger = logger;

    private const string DefaultEndpointUrl = "https://generativelanguage.googleapis.com/";
    private const string DefaultModelId = "gemini-2.0-flash";

    public string Type => ModelProviderKeys.Google;

    public string? DefaultEndpoint => DefaultEndpointUrl;

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
            throw new InvalidOperationException("API key is required for Google Gemini.");
        }

        var client = BuildClient(config, apiKey);
        var model = ResolveModelId(config);
        var configured = client.AsIChatClient(model).AsBuilder()
            .ConfigureOptions(options =>
            {
                // The SDK maps effort and thought output to its native thinking configuration.
                options.Reasoning ??= config.ChatOptions.Clone().Reasoning;

                // Disabled reasoning cannot produce a thinking summary.
                if (options.Reasoning?.Effort == ReasoningEffort.None)
                {
                    options.Reasoning.Output = null;
                }
                else if (config.SupportsThinking)
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
        throw new NotSupportedException("Google Gemini embedding generation is not enabled for this provider.");
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
            using var client = BuildClient(config, apiKey);
            var pager = await client.Models.ListAsync(
                new ListModelsConfig
                {
                    QueryBase = true,
                    PageSize = 100
                },
                cancellationToken).ConfigureAwait(false);

            // The Gemini listing carries the input-token limit, which maps to the context window.
            // Dedupe by id (keep the first descriptor so its metadata survives), then order by id.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var descriptors = new List<ModelDescriptor>();
            await foreach (var model in pager.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var modelName = NormalizeModelName(model.Name);
                if (string.IsNullOrWhiteSpace(modelName) || !seen.Add(modelName))
                {
                    continue;
                }

                descriptors.Add(new ModelDescriptor
                {
                    Id = modelName,
                    ContextWindowSize = model.InputTokenLimit is int limit && limit > 0 ? limit : null
                });
            }

            return descriptors
                .OrderBy(descriptor => descriptor.Id, StringComparer.Ordinal)
                .ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch Google Gemini models.");
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
            return (false, "API key is required for Google Gemini.");
        }

        try
        {
            using var client = BuildClient(config, apiKey);
            var modelId = ResolveModelId(config);
            var pager = await client.Models.ListAsync(
                new ListModelsConfig
                {
                    QueryBase = true,
                    PageSize = 100
                },
                cancellationToken).ConfigureAwait(false);

            await foreach (var model in pager.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(NormalizeModelName(model.Name), modelId, StringComparison.Ordinal))
                {
                    return (true, "Connected to Google Gemini successfully.");
                }
            }

            return (false, $"Connected to Google Gemini, but model '{modelId}' was not found.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Google Gemini health check failed.");
            return (false, ex.Message);
        }
    }

    private static Client BuildClient(ModelProviderConfig config, string apiKey)
    {
        return new Client(
            apiKey: apiKey,
            httpOptions: BuildHttpOptions(config));
    }

    private static HttpOptions? BuildHttpOptions(ModelProviderConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            return null;
        }

        return new HttpOptions
        {
            BaseUrl = config.BaseUrl
        };
    }

    private static string ResolveModelId(ModelProviderConfig config)
    {
        return string.IsNullOrWhiteSpace(config.ModelId) ? DefaultModelId : config.ModelId!;
    }

    private static string? NormalizeModelName(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return null;
        }

        const string prefix = "models/";
        return modelName.StartsWith(prefix, StringComparison.Ordinal)
            ? modelName[prefix.Length..]
            : modelName;
    }
}
