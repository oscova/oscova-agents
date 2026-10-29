using Microsoft.Extensions.Logging;
using Oscova.Agents.Llm;

namespace Oscova.Agents.Infrastructure.Chat;

/// <summary>
/// Custom OpenAI-compatible provider for user-supplied local or hosted endpoints.
/// </summary>
public sealed class OpenAiCompatibleModelProvider(
    ILogger<OpenAiCompatibleModelProvider> logger,
    IHttpClientFactory httpClientFactory) : OpenAiCompatibleModelProviderBase(logger, httpClientFactory)
{
    private static readonly Uri DefaultEndpointUriValue = new("https://api.openai.com/v1/");

    public override string Type => ModelProviderKeys.OpenAICompatible;

    protected override Uri? DefaultEndpointUri => DefaultEndpointUriValue;

    protected override bool RequiresApiKey => false;
}
