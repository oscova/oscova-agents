using ElevenLabs;
using Microsoft.Extensions.AI;
using SpeechToTextClient = Microsoft.Extensions.AI.ISpeechToTextClient;
using Oscova.Agents.Speech;

namespace Oscova.Agents.Plugins.ElevenLabs;

/// <summary>Transcribes completed microphone recordings through the selected user's speech credential.</summary>
public sealed class ElevenLabsSpeechRecognitionProvider(Func<HttpClient> createHttpClient) : ISpeechRecognitionProvider
{
    public string Type => ElevenLabsAgentPlugin.ProviderType;
    public string? DefaultModel => "scribe_v2";
    public string? DefaultEndpoint => ElevenLabsSpeechClient.DefaultEndpoint;
    public string? ModelPath => null;

    /// <inheritdoc />
    public async Task<SpeechTranscription> TranscribeAsync(SpeechProviderConfig config, string? apiKey, byte[] wavBytes,
        string language = "auto", CancellationToken ct = default)
    {
        if (wavBytes.Length == 0) { throw new ArgumentException("Audio payload is empty.", nameof(wavBytes)); }
        var model = string.IsNullOrWhiteSpace(config.ModelId) ? DefaultModel! : config.ModelId;
        if (model is not ("scribe_v1" or "scribe_v2"))
        {
            throw new ArgumentException($"Recognition model '{model}' is unsupported. Choose scribe_v1 or scribe_v2.", nameof(config));
        }
        using var client = ElevenLabsSpeechClient.Create(createHttpClient, config, apiKey);
        using var audio = new MemoryStream(wavBytes, writable: false);
        // The shipped speech adapter uses the experimental Microsoft speech contract.
#pragma warning disable MEAI001
        var options = new SpeechToTextOptions
        {
            ModelId = model,
            SpeechLanguage = string.IsNullOrWhiteSpace(language) || string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase) ? null : language,
            // Explicit request shaping preserves filename and disables event tags and speaker diarization.
            RawRepresentationFactory = _ => new BodySpeechToTextV1SpeechToTextPost
            {
                ModelId = model == "scribe_v2" ? BodySpeechToTextV1SpeechToTextPostModelId.ScribeV2 : BodySpeechToTextV1SpeechToTextPostModelId.ScribeV1,
                File = wavBytes, Filename = "speech.wav", TagAudioEvents = false, Diarize = false
            }
        };
        try
        {
            var result = await ((SpeechToTextClient)client.SpeechToText2).GetTextAsync(audio, options, ct).ConfigureAwait(false);
            return new SpeechTranscription { Text = result.Text, Segments = [] };
        }
        catch (ApiException ex) { throw ElevenLabsSpeechClient.Failure("recognition", ex); }
#pragma warning restore MEAI001
    }

    /// <inheritdoc />
    public Task<bool> IsAvailableAsync(SpeechProviderConfig config, string? apiKey, CancellationToken ct = default) => Task.FromResult(!string.IsNullOrWhiteSpace(apiKey));
    /// <inheritdoc />
    public Task StartDownloadAsync(SpeechProviderConfig config, string? modelDownloadUrl, CancellationToken ct = default) => Task.CompletedTask;
}
