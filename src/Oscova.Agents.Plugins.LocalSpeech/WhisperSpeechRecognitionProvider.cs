using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Oscova.Agents.Speech;
using Whisper.net;
#if LOCAL_SPEECH_CUDA
using Whisper.net.LibraryLoader;
#endif

namespace Oscova.Agents.Plugins.LocalSpeech;

/// <summary>Runs local speech recognition through the packaged Whisper runtime.</summary>
internal sealed class WhisperSpeechRecognitionProvider : ISpeechRecognitionProvider, IDisposable
{
    private const string DefaultModelUrl = "https://huggingface.co/sandrohanea/whisper.net/resolve/v3/classic/ggml-base.bin";
    private readonly Func<string, string, string, CancellationToken, Task> _ensureDownloadAsync;
    private readonly ILogger<WhisperSpeechRecognitionProvider> _logger;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private readonly SemaphoreSlim _runGate = new(Environment.ProcessorCount > 1 ? 2 : 1, 2);
    private WhisperFactory? _factory;

    public WhisperSpeechRecognitionProvider(
        string modelPath,
        Func<string, string, string, CancellationToken, Task> ensureDownloadAsync,
        ILogger<WhisperSpeechRecognitionProvider> logger)
    {
        ModelPath = modelPath;
        _ensureDownloadAsync = ensureDownloadAsync;
        _logger = logger;
    }

    public string Type => SpeechProviderKeys.Local;

    public string? DefaultEndpoint => null;

    public string ModelPath { get; }

    public Task StartDownloadAsync(
        SpeechProviderConfig config,
        string? modelDownloadUrl,
        CancellationToken ct = default)
    {
        var url = string.IsNullOrWhiteSpace(modelDownloadUrl) ? DefaultModelUrl : modelDownloadUrl;
        return _ensureDownloadAsync("asr-model", url, ModelPath, ct);
    }

    public async Task<SpeechTranscription> TranscribeAsync(
        SpeechProviderConfig config,
        string? apiKey,
        byte[] wavBytes,
        string language = "auto",
        CancellationToken ct = default)
    {
        if (wavBytes is null || wavBytes.Length == 0)
        {
            throw new ArgumentException("Audio payload is empty.", nameof(wavBytes));
        }

        await EnsureFactoryAsync(ct).ConfigureAwait(false);
        await _runGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var input = new MemoryStream(wavBytes, writable: false);
            using var reader = new WaveFileReader(input);
            ISampleProvider samples = reader.ToSampleProvider();
            if (samples.WaveFormat.Channels > 1)
            {
                samples = new StereoToMonoSampleProvider(samples);
            }

            var resampled = new WdlResamplingSampleProvider(samples, 16000);
            using var wav16k = new MemoryStream();
            WaveFileWriter.WriteWavFileToStream(wav16k, resampled.ToWaveProvider16());
            wav16k.Position = 0;

            var stopwatch = Stopwatch.StartNew();
            var transcription = new SpeechTranscription
            {
                Text = string.Empty,
                Segments = []
            };

            using var processor = _factory!.CreateBuilder()
                .WithLanguage(language)
                .Build();

            await foreach (var result in processor.ProcessAsync(wav16k, ct).ConfigureAwait(false))
            {
                if (result is null)
                {
                    continue;
                }

                transcription.Text += result.Text;
                if (!string.IsNullOrWhiteSpace(result.Text))
                {
                    transcription.Segments.Add(result.Text);
                }
            }

            stopwatch.Stop();
            _logger.LogDebug(
                "ASR completed in {ElapsedMs} ms | Mode={Backend} | Lang={Lang} | Segments={Segments} | TextLen={Length}",
                stopwatch.ElapsedMilliseconds,
                LocalSpeechAgentPlugin.Backend,
                language,
                transcription.Segments.Count,
                transcription.Text.Length);
            return transcription;
        }
        finally
        {
            _runGate.Release();
        }
    }

    public Task<bool> IsAvailableAsync(
        SpeechProviderConfig config,
        string? apiKey,
        CancellationToken ct = default)
    {
        return Task.FromResult(File.Exists(ModelPath));
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _initGate.Dispose();
        _runGate.Dispose();
    }

    private async Task EnsureFactoryAsync(CancellationToken ct)
    {
        if (_factory is not null)
        {
            return;
        }

        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_factory is not null)
            {
                return;
            }

            if (!File.Exists(ModelPath))
            {
                _logger.LogWarning("Speech recognition model not available at path: {ModelPath}", ModelPath);
                throw new SpeechModelUnavailableException("Speech recognition model is not available.");
            }

#if LOCAL_SPEECH_CUDA
            try
            {
                var runtimeInfo = WhisperFactory.GetRuntimeInfo();
                // The selected distribution alone does not prove that the CUDA backend registered.
                if (RuntimeOptions.LoadedLibrary != RuntimeLibrary.Cuda ||
                    runtimeInfo?.Contains("CUDA : ", StringComparison.Ordinal) != true)
                    throw new InvalidOperationException("Whisper did not register its CUDA backend.");

                _factory = WhisperFactory.FromPath(ModelPath);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    "NVIDIA speech recognition could not initialize CUDA. Check the NVIDIA driver and CUDA 13 installation, or install the CPU speech edition and restart.",
                    exception);
            }
#else
            _factory = WhisperFactory.FromPath(ModelPath);
#endif
        }
        finally
        {
            _initGate.Release();
        }
    }
}
