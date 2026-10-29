using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Credentials;
using Oscova.Agents.Llm;

namespace Oscova.Agents.Infrastructure.Chat;

/// <summary>
/// Groq model provider using its OpenAI-compatible chat-completions endpoint.
/// </summary>
[CredentialSlot(Key = "groq", Label = "Groq API Key", Description = "API key used by Groq model providers.")]
public sealed class GroqModelProvider(
    ILogger<GroqModelProvider> logger,
    IHttpClientFactory httpClientFactory) : OpenAiCompatibleModelProviderBase(logger, httpClientFactory)
{
    private static readonly Uri DefaultEndpointUriValue = new("https://api.groq.com/openai/v1/");

    public override string Type => ModelProviderKeys.Groq;

    // Groq serves no embedding models, so it stays out of the embedding provider choices.
    public override bool SupportsEmbedding => false;

    protected override Uri? DefaultEndpointUri => DefaultEndpointUriValue;

    public override ValueTask<IEmbeddingGenerator<string, Embedding<float>>> CreateEmbeddingGeneratorAsync(
        ModelProviderConfig config,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Groq embedding generation is not enabled for this provider.");
    }
}
