using ElevenLabs;
using Oscova.Agents.Speech;
using Oscova.Agents.Speech.Contracts;
using Oscova.Agents.Plugins.Shared.Speech;
using System.Diagnostics;

namespace Oscova.Agents.Plugins.ElevenLabs;

/// <summary>Produces browser and Avatar-compatible speech from an explicitly selected account voice.</summary>
public sealed class ElevenLabsSpeechSynthesisProvider(Func<HttpClient> createHttpClient) : ISpeechSynthesisProvider
{
    private const int SampleRate = 24000;
    private const short Channels = 1;
    private const short BitsPerSample = 16;
    public string Type => ElevenLabsAgentPlugin.ProviderType;
    public string? DefaultModel => "eleven_flash_v2_5";
    public string? DefaultVoiceId => null;
    public string? DefaultEndpoint => ElevenLabsSpeechClient.DefaultEndpoint;
    public string? ModelPath => null;

    /// <inheritdoc />
    public Task StartSessionAsync(string userId, SpeechProviderConfig config, string? apiKey, string? voiceId, CancellationToken ct = default)
    {
        using var client = ElevenLabsSpeechClient.Create(createHttpClient, config, apiKey);
        return Task.CompletedTask;
    }
    /// <inheritdoc />
    public Task StopSessionAsync(string userId, SpeechProviderConfig config, CancellationToken ct = default) => Task.CompletedTask;
    /// <inheritdoc />
    public Task<bool> IsAvailableAsync(SpeechProviderConfig config, string? apiKey, CancellationToken ct = default) => Task.FromResult(!string.IsNullOrWhiteSpace(apiKey));
    /// <inheritdoc />
    public Task StartDownloadAsync(SpeechProviderConfig config, string? modelDownloadUrl, CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public async Task<IReadOnlyList<SpeechSynthesisVoice>> GetAvailableVoicesAsync(SpeechProviderConfig config, string? apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) { return []; }
        using var client = ElevenLabsSpeechClient.Create(createHttpClient, config, apiKey);
        var voices = new List<SpeechSynthesisVoice>();
        try
        {
            await foreach (var voice in client.Voices.SearchAutoPagingAsync(cancellationToken: ct).ConfigureAwait(false))
            {
                voices.Add(new(voice.VoiceId, voice.Name ?? voice.VoiceId));
            }
            return voices;
        }
        catch (ApiException ex) { throw ElevenLabsSpeechClient.Failure("voice discovery", ex); }
    }

    /// <inheritdoc />
    public async Task<SpeechSynthesisResponse> SynthesizeAsync(string userId, SpeechProviderConfig config, string? apiKey,
        SpeechSynthesisRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text)) { throw new ArgumentException("Text cannot be empty.", nameof(request)); }
        var voiceId = string.IsNullOrWhiteSpace(request.VoiceId) ? config.VoiceId : request.VoiceId;
        if (string.IsNullOrWhiteSpace(voiceId)) { throw new InvalidOperationException("Select an accessible ElevenLabs account voice in speech settings."); }
        using var client = ElevenLabsSpeechClient.Create(createHttpClient, config, apiKey);
        var started = Stopwatch.StartNew();
        byte[] pcm;
        try
        {
            // PCM gives deterministic sample metadata and uses the same WAV writer as local synthesis.
            pcm = await client.TextToSpeech.ConvertAsync(voiceId, new BodyTextToSpeechFull
            {
                Text = request.Text, ModelId = string.IsNullOrWhiteSpace(config.ModelId) ? DefaultModel : config.ModelId
            }, outputFormat: TextToSpeechFullOutputFormat.Pcm24000, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (ApiException ex) { throw ElevenLabsSpeechClient.Failure("synthesis", ex); }
        if (pcm.Length == 0 || pcm.Length % sizeof(short) != 0)
        {
            throw new InvalidDataException("ElevenLabs returned empty audio or an incomplete 16-bit PCM sample.");
        }
        return new SpeechSynthesisResponse
        {
            Audio = request.IncludeWavHeader ? PcmWaveWriter.Write(pcm, SampleRate, Channels, BitsPerSample) : pcm,
            SampleRate = SampleRate, Channels = Channels, BitsPerSample = BitsPerSample,
            TotalSamples = pcm.Length / sizeof(short), VoiceId = voiceId, DurationMs = started.Elapsed.TotalMilliseconds
        };
    }
}
