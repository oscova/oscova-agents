using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Audio;
using Oscova.Agents.Speech;
using System.ClientModel;
using System.ClientModel.Primitives;

namespace Oscova.Agents.Plugins.OpenAI;

/// <summary>
/// Uses OpenAI's file transcription API as a hosted speech recognition provider.
/// </summary>
public sealed class OpenAiSpeechRecognitionProvider(HttpClient http, ILogger<OpenAiSpeechRecognitionProvider> logger) : ISpeechRecognitionProvider, IDisposable
{
    /// <summary>Identifies the default transcription model.</summary>
    public const string DefaultModelId = "gpt-4o-mini-transcribe";
    /// <inheritdoc />
    public string? DefaultModel => DefaultModelId;

    public string Type => SpeechProviderKeys.OpenAI;

    public string? DefaultEndpoint => "https://api.openai.com/v1/";

    public string? ModelPath => null;

    public async Task<SpeechTranscription> TranscribeAsync(
        SpeechProviderConfig config,
        string? apiKey,
        byte[] wavBytes,
        string language = "auto",
        CancellationToken ct = default)
    {
        if (wavBytes.Length == 0)
        {
            throw new ArgumentException("Audio payload is empty.", nameof(wavBytes));
        }

        var client = CreateClient(config, apiKey);
        var stream = new MemoryStream(wavBytes, writable: false);
        await using (stream.ConfigureAwait(false))
        {
            var options = new AudioTranscriptionOptions
            {
                ResponseFormat = AudioTranscriptionFormat.Simple
            };

            if (!string.IsNullOrWhiteSpace(language) &&
                !string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase))
            {
                options.Language = language;
            }

            var result = await client.TranscribeAudioAsync(stream, "speech.wav", options, ct).ConfigureAwait(false);
            return new SpeechTranscription
            {
                Text = result.Value.Text ?? string.Empty,
                Segments = []
            };
        }
    }

    public Task<bool> IsAvailableAsync(SpeechProviderConfig config, string? apiKey, CancellationToken ct = default)
    {
        return Task.FromResult(!string.IsNullOrWhiteSpace(apiKey));
    }

    public Task StartDownloadAsync(SpeechProviderConfig config, string? modelDownloadUrl, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    private AudioClient CreateClient(SpeechProviderConfig config, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException($"Credential '{config.CredentialKey}' is not available for speech recognition provider '{config.Name}'.");
        }

        var model = string.IsNullOrWhiteSpace(config.ModelId)
            ? DefaultModelId
            : config.ModelId!;

        try
        {
            return new AudioClient(model, new ApiKeyCredential(apiKey), BuildClientOptions(config));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to create OpenAI speech recognition client for {Provider}.", config.Name);
            throw;
        }
    }

    // The Host guards requests, while each SDK client receives only this invocation's key.
    private OpenAIClientOptions BuildClientOptions(SpeechProviderConfig config)
    {
        var options = new OpenAIClientOptions { Transport = new HttpClientPipelineTransport(http) };
        if (!string.IsNullOrWhiteSpace(config.BaseUrl) &&
            Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out var endpoint))
        {
            options.Endpoint = endpoint;
        }
        return options;
    }

    /// <summary>Releases the provider's host-managed HTTP client.</summary>
    public void Dispose() => http.Dispose();
}
