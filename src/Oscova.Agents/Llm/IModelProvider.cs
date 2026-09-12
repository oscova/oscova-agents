using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace Oscova.Agents.Llm;

/// <summary>
/// Creates clients and metadata for one language model provider family.
/// </summary>
public interface IModelProvider
{
    /// <summary>
    /// Gets the provider type handled by this implementation.
    /// </summary>
    string Type { get; }

    /// <summary>
    /// Gets the default API endpoint, or null for in-process providers.
    /// </summary>
    string? DefaultEndpoint { get; }

    /// <summary>
    /// Gets the default model identifier, or null when the user must choose one.
    /// </summary>
    string? DefaultModel { get; }

    /// <summary>
    /// Gets the provider's default endpoint locality, or null for endpoint-based detection.
    /// </summary>
    bool? DefaultIsLocalEndpoint { get; }

    /// <summary>
    /// Gets whether this provider supports embeddings.
    /// </summary>
    bool SupportsEmbedding { get; }

    /// <summary>
    /// Creates a chat client for the supplied configuration.
    /// </summary>
    ValueTask<IChatClient> CreateClientAsync(
        ModelProviderConfig config,
        string? apiKey = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an embedding generator for the supplied configuration.
    /// </summary>
    ValueTask<IEmbeddingGenerator<string, Embedding<float>>> CreateEmbeddingGeneratorAsync(
        ModelProviderConfig config,
        string? apiKey = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the available models for the supplied configuration, including any metadata the
    /// provider can report (context window, capabilities). Providers that expose only ids return
    /// <see cref="ModelDescriptor"/> instances with null metadata.
    /// </summary>
    Task<IReadOnlyList<ModelDescriptor>> GetAvailableModelsAsync(
        ModelProviderConfig config,
        string? apiKey = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Tests the supplied provider configuration.
    /// </summary>
    Task<(bool Success, string Message)> TestAsync(
        ModelProviderConfig config,
        string? apiKey = null,
        CancellationToken cancellationToken = default);
}
