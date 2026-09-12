using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using KokoroSharp;
using KokoroSharp.Core;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Speech;
using Oscova.Agents.Speech.Contracts;

namespace Oscova.Agents.Plugins.LocalSpeech;

/// <summary>Runs local speech synthesis through Kokoro and the packaged voice assets.</summary>
internal sealed class KokoroSpeechSynthesisProvider : ISpeechSynthesisProvider, IDisposable
{
    private const int DefaultSampleRate = 24000;
    private const short DefaultChannels = 1;
    private const short DefaultBitsPerSample = 16;
    private const string DefaultVoiceId = "af_heart";
    private const string DefaultModelUrl = "https://github.com/taylorchu/kokoro-onnx/releases/download/v0.2.0/kokoro.onnx";
    private readonly ConcurrentDictionary<string, UserEntry> _sessions = new(StringComparer.Ordinal);
    private readonly Func<string, string, string, CancellationToken, Task> _ensureDownloadAsync;
    private readonly ILogger<KokoroSpeechSynthesisProvider> _logger;

    public KokoroSpeechSynthesisProvider(
        string modelPath,
        Func<string, string, string, CancellationToken, Task> ensureDownloadAsync,
        ILogger<KokoroSpeechSynthesisProvider> logger)
    {
        ModelPath = modelPath;
        _ensureDownloadAsync = ensureDownloadAsync;
        _logger = logger;
    }

    public string Type => SpeechProviderKeys.Local;

    public string? DefaultEndpoint => null;

    public string ModelPath { get; }

    public Task StartSessionAsync(
        string userId,
        SpeechProviderConfig config,
        string? apiKey,
        string? voiceId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("userId required", nameof(userId));
        }

        var selectedVoice = string.IsNullOrWhiteSpace(voiceId) ? DefaultVoiceId : voiceId;
        _sessions.AddOrUpdate(
            userId,
            _ => new UserEntry(ModelPath, selectedVoice),
            (_, existing) =>
            {
                existing.SetVoice(selectedVoice);
                return existing;
            });
        return Task.CompletedTask;
    }

    public Task StopSessionAsync(
        string userId,
        SpeechProviderConfig config,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(userId) && _sessions.TryRemove(userId, out var entry))
        {
            entry.Dispose();
        }

        return Task.CompletedTask;
    }

    public Task<bool> IsAvailableAsync(
        SpeechProviderConfig config,
        string? apiKey,
        CancellationToken ct = default)
    {
        return Task.FromResult(File.Exists(ModelPath));
    }

    public Task<IReadOnlyList<SpeechSynthesisVoice>> GetAvailableVoicesAsync(
        SpeechProviderConfig config,
        string? apiKey,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<SpeechSynthesisVoice> voices = [.. KokoroVoiceManager.Voices
            // Kokoro v1.1 Mandarin voices require a separate model that this plugin does not install.
            .Where(voice => voice.Language != KokoroLanguage.MandarinChinese)
            .DistinctBy(voice => voice.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(voice => voice.Name, StringComparer.OrdinalIgnoreCase)
            .Select(voice => new SpeechSynthesisVoice(
                voice.Name,
                FormatVoiceName(voice.Name),
                FormatLanguage(voice.Language),
                voice.Gender.ToString()))];
        return Task.FromResult(voices);
    }

    private static string FormatVoiceName(string voiceId) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(voiceId[3..].Replace('_', ' '));

    private static string FormatLanguage(KokoroLanguage language) => language switch
    {
        KokoroLanguage.AmericanEnglish => "American English",
        KokoroLanguage.BritishEnglish => "British English",
        KokoroLanguage.BrazilianPortuguese => "Brazilian Portuguese",
        _ => language.ToString()
    };

    public async Task<SpeechSynthesisResponse> SynthesizeAsync(
        string userId,
        SpeechProviderConfig config,
        string? apiKey,
        SpeechSynthesisRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("userId required", nameof(userId));
        }

        if (string.IsNullOrWhiteSpace(request.Text))
        {
            throw new ArgumentException("Text cannot be empty.", nameof(request));
        }

        if (!File.Exists(ModelPath))
        {
            throw new SpeechModelUnavailableException("TTS model 'kokoro.onnx' is not available yet.");
        }

        if (!_sessions.TryGetValue(userId, out var entry))
        {
            throw new SpeechSynthesisSessionUnavailableException(
                $"No active TTS session for session ID '{userId}'. Session may have been stopped.");
        }

        var voiceId = string.IsNullOrWhiteSpace(request.VoiceId) ? DefaultVoiceId : request.VoiceId;
        entry.SetVoice(voiceId);
        var stopwatch = Stopwatch.StartNew();
        List<short> pcm16 = [];
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnProgress(float[] samples)
        {
            foreach (var sample in samples)
            {
                pcm16.Add((short)(Math.Clamp(sample, -1f, 1f) * short.MaxValue));
            }
        }

        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            entry.Synthesizer.Synthesize(request.Text, entry.Voice, OnProgress, () => completion.TrySetResult());
            await completion.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            entry.Gate.Release();
        }

        var wavBytes = BuildWav(pcm16);
        var audioBytes = request.IncludeWavHeader ? wavBytes : BuildRawPcm(pcm16);
        stopwatch.Stop();
        _logger.LogDebug(
            "TTS completed in {ElapsedMs} ms | Mode=CPU | UserId={UserId} | Voice={Voice} | TextLen={TextLen} | Samples={Samples} | WavHeader={WavHeader}",
            stopwatch.ElapsedMilliseconds,
            userId,
            voiceId,
            request.Text.Length,
            pcm16.Count,
            request.IncludeWavHeader);

        return new SpeechSynthesisResponse
        {
            Audio = audioBytes,
            SampleRate = DefaultSampleRate,
            Channels = DefaultChannels,
            BitsPerSample = DefaultBitsPerSample,
            TotalSamples = pcm16.Count,
            VoiceId = voiceId,
            DurationMs = stopwatch.Elapsed.TotalMilliseconds
        };
    }

    public Task StartDownloadAsync(
        SpeechProviderConfig config,
        string? modelDownloadUrl,
        CancellationToken ct = default)
    {
        var url = string.IsNullOrWhiteSpace(modelDownloadUrl) ? DefaultModelUrl : modelDownloadUrl;
        return _ensureDownloadAsync("tts-model", url, ModelPath, ct);
    }

    public void Dispose()
    {
        foreach (var entry in _sessions.Values)
        {
            entry.Dispose();
        }

        _sessions.Clear();
    }

    private static byte[] BuildWav(List<short> pcm16)
    {
        var dataBytes = pcm16.Count * sizeof(short);
        var buffer = new byte[44 + dataBytes];
        var span = buffer.AsSpan();
        Encoding.ASCII.GetBytes("RIFF").CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(4), buffer.Length - 8);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(span.Slice(8));
        Encoding.ASCII.GetBytes("fmt ").CopyTo(span.Slice(12));
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(22), DefaultChannels);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(24), DefaultSampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(
            span.Slice(28),
            DefaultSampleRate * DefaultChannels * DefaultBitsPerSample / 8);
        BinaryPrimitives.WriteInt16LittleEndian(
            span.Slice(32),
            (short)(DefaultChannels * DefaultBitsPerSample / 8));
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(34), DefaultBitsPerSample);
        Encoding.ASCII.GetBytes("data").CopyTo(span.Slice(36));
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(40), dataBytes);
        Buffer.BlockCopy(pcm16.ToArray(), 0, buffer, 44, dataBytes);
        return buffer;
    }

    private static byte[] BuildRawPcm(List<short> pcm16)
    {
        var bytes = new byte[pcm16.Count * sizeof(short)];
        Buffer.BlockCopy(pcm16.ToArray(), 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private sealed class UserEntry : IDisposable
    {
        public UserEntry(string modelPath, string voiceId)
        {
            Synthesizer = new KokoroWavSynthesizer(modelPath);
            SetVoice(voiceId);
        }

        public KokoroWavSynthesizer Synthesizer { get; }

        public KokoroVoice Voice { get; private set; } = null!;

        public string VoiceId { get; private set; } = string.Empty;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public void SetVoice(string voiceId)
        {
            if (string.Equals(VoiceId, voiceId, StringComparison.Ordinal))
            {
                return;
            }

            VoiceId = voiceId;
            Voice = KokoroVoiceManager.GetVoice(voiceId);
        }

        public void Dispose()
        {
            Synthesizer.Dispose();
            Gate.Dispose();
        }
    }
}
