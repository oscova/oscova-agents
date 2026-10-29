using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;
using Oscova.Agents.Credentials;
using Oscova.Agents.Llm;

namespace Oscova.Agents.Infrastructure.Chat;

/// <summary>
/// NVIDIA NIM chat provider; the hosted free endpoint can intermittently return empty successful streams.
/// </summary>
[CredentialSlot(Key = "nvidia-nim", Label = "NVIDIA NIM API Key", Description = "API key used by NVIDIA NIM model providers.")]
public sealed class NvidiaNimModelProvider(
    ILogger<NvidiaNimModelProvider> logger,
    IHttpClientFactory httpClientFactory) : OpenAiCompatibleModelProviderBase(logger, httpClientFactory)
{
    private static readonly Uri DefaultEndpointUriValue = new("https://integrate.api.nvidia.com/v1/");

    public override string Type => ModelProviderKeys.NvidiaNim;

    public override string? DefaultModel => "nvidia/nemotron-3.5-lightning-30b-a3b";

    public override bool SupportsEmbedding => false;

    protected override Uri? DefaultEndpointUri => DefaultEndpointUriValue;

    protected override void ApplyRequestDefaults(ChatOptions options, ModelProviderConfig config)
    {
        base.ApplyRequestDefaults(options, config);
        if (config.SupportsThinking) { return; }

        // Hybrid NIM models reason until the chat template disables it, and MEAI has no field for template arguments.
#pragma warning disable SCME0001
        options.RawRepresentationFactory ??= _ =>
        {
            var raw = new ChatCompletionOptions();
            raw.Patch.Set("$.chat_template_kwargs.enable_thinking"u8, false);
            return raw;
        };
#pragma warning restore SCME0001
    }

    public override ValueTask<IEmbeddingGenerator<string, Embedding<float>>> CreateEmbeddingGeneratorAsync(
        ModelProviderConfig config,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("NVIDIA NIM embedding generation is not enabled for this provider.");
    }
}
