using OpenAI;
using OpenAI.Audio;
using Oscova.Agents.Speech;
using Oscova.Agents.Speech.Contracts;
using Oscova.Agents.Plugins.Shared.Speech;
using System.ClientModel;
using System.ClientModel.Primitives;

namespace Oscova.Agents.Plugins.OpenAI;

/// <summary>
/// Uses OpenAI's speech generation API as a hosted speech synthesis provider.
/// </summary>
public sealed class OpenAiSpeechSynthesisProvider(HttpClient http) : ISpeechSynthesisProvider, IDisposable
{
    /// <summary>Identifies the default synthesis voice.</summary>
    public const string DefaultVoice = "alloy";
    /// <inheritdoc />
    public string? DefaultVoiceId => DefaultVoice;

    private const int DefaultSampleRate = 24000;
    private const short DefaultChannels = 1;
    private const short DefaultBitsPerSample = 16;
    /// <summary>Identifies the default synthesis model.</summary>
    public const string DefaultModelId = "gpt-4o-mini-tts";
    /// <inheritdoc />
    public string? DefaultModel => DefaultModelId;

    public string Type => SpeechProviderKeys.OpenAI;

    public string? DefaultEndpoint => "https://api.openai.com/v1/";

    public string? ModelPath => null;

    public Task StartSessionAsync(string userId, SpeechProviderConfig config, string? apiKey, string? voiceId, CancellationToken ct = default)
    {
        _ = CreateClient(config, apiKey);
        return Task.CompletedTask;
    }

    public Task StopSessionAsync(string userId, SpeechProviderConfig config, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task<bool> IsAvailableAsync(SpeechProviderConfig config, string? apiKey, CancellationToken ct = default)
    {
        return Task.FromResult(!string.IsNullOrWhiteSpace(apiKey));
    }

    public Task<IReadOnlyList<SpeechSynthesisVoice>> GetAvailableVoicesAsync(SpeechProviderConfig config, string? apiKey, CancellationToken ct = default)
    {
        IReadOnlyList<SpeechSynthesisVoice> voices =
        [
            new("alloy", "Alloy"),
            new("echo", "Echo"),
            new("fable", "Fable"),
            new("onyx", "Onyx"),
            new("nova", "Nova"),
            new("shimmer", "Shimmer")
        ];
        return Task.FromResult(voices);
    }

    public async Task<SpeechSynthesisResponse> SynthesizeAsync(
        string userId,
        SpeechProviderConfig config,
        string? apiKey,
        SpeechSynthesisRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            throw new ArgumentException("Text cannot be empty.", nameof(request));
        }

        var voiceId = string.IsNullOrWhiteSpace(request.VoiceId)
            ? (string.IsNullOrWhiteSpace(config.VoiceId) ? DefaultVoice : config.VoiceId)
            : request.VoiceId!;

        var client = CreateClient(config, apiKey);
        var voice = ResolveVoice(voiceId);
        var startedAt = DateTimeOffset.UtcNow;
        var options = new SpeechGenerationOptions
        {
            ResponseFormat = GeneratedSpeechFormat.Pcm
        };

        var speech = await client.GenerateSpeechAsync(request.Text, voice, options, ct).ConfigureAwait(false);
        // PCM avoids treating optional WAV metadata chunks as audio samples.
        var pcm = speech.Value.ToArray();
        var audio = request.IncludeWavHeader
            ? PcmWaveWriter.Write(pcm, DefaultSampleRate, DefaultChannels, DefaultBitsPerSample)
            : pcm;

        return new SpeechSynthesisResponse
        {
            Audio = audio,
            SampleRate = DefaultSampleRate,
            Channels = DefaultChannels,
            BitsPerSample = DefaultBitsPerSample,
            TotalSamples = pcm.Length / sizeof(short),
            VoiceId = voiceId,
            DurationMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds
        };
    }

    public Task StartDownloadAsync(SpeechProviderConfig config, string? modelDownloadUrl, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    private AudioClient CreateClient(SpeechProviderConfig config, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException($"Credential '{config.CredentialKey}' is not available for speech synthesis provider '{config.Id}'.");
        }

        var model = string.IsNullOrWhiteSpace(config.ModelId)
            ? DefaultModelId
            : config.ModelId!;
        return new AudioClient(model, new ApiKeyCredential(apiKey), BuildClientOptions(config));
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

    private static GeneratedSpeechVoice ResolveVoice(string voiceId)
    {
        return string.IsNullOrWhiteSpace(voiceId)
            ? GeneratedSpeechVoice.Alloy
            : new GeneratedSpeechVoice(voiceId.Trim());
    }

}
