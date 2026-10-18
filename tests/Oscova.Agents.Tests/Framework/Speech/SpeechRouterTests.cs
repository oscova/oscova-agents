using Oscova.Agents.Speech;
using Oscova.Agents.Speech.Contracts;

namespace Oscova.Agents.Tests.Framework.Speech;

public sealed class SpeechRouterTests
{
    /// <summary>An active session remains bound to its original provider when the selected configuration changes.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveSession_ProviderChangePreservesSynthesisAndCleanupOwner(bool changeDuringStart)
    {
        var original = new RecordingSynthesisProvider("original", "https://original.example");
        var replacement = new RecordingSynthesisProvider("replacement", "https://replacement.example");
        var router = new SpeechSynthesisRouter(new TestSpeechProviderCatalog(synthesis: [original, replacement]));
        var config = new SpeechProviderConfig { Type = "original", ModelId = "first" };
        if (changeDuringStart) { original.StartGate = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        var starting = router.StartSessionAsync(config, "original-key", "owner:session", "voice", false);
        if (!changeDuringStart) { await starting; }
        config.Type = "replacement";
        config.ModelId = "second";
        original.StartGate?.SetResult();
        await starting;
        await router.SynthesizeAsync(config, "replacement-key", "owner:session", new() { Text = "Use the started session.", VoiceId = "replacement-voice" }, false);
        await router.StopSessionAsync(config, "owner:session", false);
        Assert.Equal(1, original.InvocationCount);
        Assert.Equal("first", original.Config?.ModelId);
        Assert.Equal("original-key", original.ApiKey);
        Assert.Equal("voice", original.Request?.VoiceId);
        Assert.Equal(1, original.StopCount);
        Assert.Equal(0, replacement.InvocationCount);
        Assert.Equal(0, replacement.StopCount);
    }

    /// <summary>Optional speech providers are unavailable when their plugin is not installed.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IsAvailableAsync_WithMissingProvider_ReturnsFalse(bool synthesis)
    {
        var catalog = new TestSpeechProviderCatalog();
        var config = new SpeechProviderConfig { Type = "Local" };

        var available = synthesis
            ? await new SpeechSynthesisRouter(catalog).IsAvailableAsync(config, null, true)
            : await new SpeechRecognitionRouter(catalog).IsAvailableAsync(config, null, true);

        Assert.False(available);
    }

    [Fact]
    public async Task TranscribeAsync_WithPluginDefinedType_ForwardsCallerInputs()
    {
        var provider = new RecordingRecognitionProvider("custom-asr", "http://127.0.0.1");
        var router = new SpeechRecognitionRouter(new TestSpeechProviderCatalog(recognition: [provider]));
        var config = new SpeechProviderConfig { Type = "custom-asr" };
        byte[] wavBytes = [1, 2, 3];

        var result = await router.TranscribeAsync(config, "secret", wavBytes, true, "fr");

        Assert.Equal("transcribed", result.Text);
        Assert.Same(config, provider.Config);
        Assert.Equal("secret", provider.ApiKey);
        Assert.Same(wavBytes, provider.WavBytes);
        Assert.Equal("fr", provider.Language);
        Assert.Equal(1, provider.InvocationCount);
    }

    [Fact]
    public async Task SynthesizeAsync_WithPluginDefinedType_ForwardsCallerInputs()
    {
        var provider = new RecordingSynthesisProvider("custom-tts", "http://127.0.0.1");
        var router = new SpeechSynthesisRouter(new TestSpeechProviderCatalog(synthesis: [provider]));
        var config = new SpeechProviderConfig { Type = "custom-tts" };
        var request = new SpeechSynthesisRequest { Text = "hello" };

        var result = await router.SynthesizeAsync(config, "secret", "user:session", request, true);

        Assert.Same(provider.Response, result);
        Assert.Same(config, provider.Config);
        Assert.Equal("secret", provider.ApiKey);
        Assert.Equal("user:session", provider.UserId);
        Assert.Same(request, provider.Request);
        Assert.Equal(1, provider.InvocationCount);
    }

    [Fact]
    public async Task GetAvailableVoicesAsync_ReturnsProviderDescriptorUnchanged()
    {
        var provider = new RecordingSynthesisProvider("custom-tts", null);
        var router = new SpeechSynthesisRouter(new TestSpeechProviderCatalog(synthesis: [provider]));

        var voices = await router.GetAvailableVoicesAsync(new SpeechProviderConfig { Type = "custom-tts" }, null, false);

        Assert.Same(provider.Voice, Assert.Single(voices));
    }

    [Fact]
    public async Task TranscribeAsync_WithCloudProviderWhileOffline_BlocksBeforeInvocation()
    {
        var provider = new RecordingRecognitionProvider("custom-asr", "http://8.8.8.8");
        var router = new SpeechRecognitionRouter(new TestSpeechProviderCatalog(recognition: [provider]));
        var config = new SpeechProviderConfig { Type = "custom-asr" };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => router.TranscribeAsync(config, null, [], true));

        Assert.Equal("Offline mode is on: this speech provider uses a non-local endpoint and is blocked.", error.Message);
        Assert.Equal(0, provider.InvocationCount);
    }

    [Fact]
    public async Task SynthesizeAsync_WithCloudProviderWhileOffline_BlocksBeforeInvocation()
    {
        var provider = new RecordingSynthesisProvider("custom-tts", "http://8.8.8.8");
        var router = new SpeechSynthesisRouter(new TestSpeechProviderCatalog(synthesis: [provider]));
        var config = new SpeechProviderConfig { Type = "custom-tts" };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => router.SynthesizeAsync(config, null, "user:session", new SpeechSynthesisRequest(), true));

        Assert.Equal("Offline mode is on: this speech provider uses a non-local endpoint and is blocked.", error.Message);
        Assert.Equal(0, provider.InvocationCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("https://example.com/models/asr")]
    public async Task RecognitionDownload_WithoutExplicitLocalUrlWhileOffline_BlocksBeforeInvocation(string? url)
    {
        var provider = new RecordingRecognitionProvider("custom-asr", "http://127.0.0.1");
        var router = new SpeechRecognitionRouter(new TestSpeechProviderCatalog(recognition: [provider]));
        var config = new SpeechProviderConfig { Type = "custom-asr" };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => router.StartDownloadAsync(config, url, true));

        Assert.Null(provider.DownloadConfig);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("https://example.com/models/tts")]
    public async Task SynthesisDownload_WithoutExplicitLocalUrlWhileOffline_BlocksBeforeInvocation(string? url)
    {
        var provider = new RecordingSynthesisProvider("custom-tts", "http://127.0.0.1");
        var router = new SpeechSynthesisRouter(new TestSpeechProviderCatalog(synthesis: [provider]));
        var config = new SpeechProviderConfig { Type = "custom-tts" };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => router.StartDownloadAsync(config, url, true));

        Assert.Null(provider.DownloadConfig);
    }

    [Fact]
    public async Task StartDownloadAsync_WithPluginDefinedTypes_ForwardsCallerInputs()
    {
        using var cts = new CancellationTokenSource();
        var recognitionProvider = new RecordingRecognitionProvider("custom-asr", null);
        var synthesisProvider = new RecordingSynthesisProvider("custom-tts", null);
        var recognitionConfig = new SpeechProviderConfig { Type = "custom-asr" };
        var synthesisConfig = new SpeechProviderConfig { Type = "custom-tts" };
        var recognitionRouter = new SpeechRecognitionRouter(new TestSpeechProviderCatalog(recognition: [recognitionProvider]));
        var synthesisRouter = new SpeechSynthesisRouter(new TestSpeechProviderCatalog(synthesis: [synthesisProvider]));

        await recognitionRouter.StartDownloadAsync(recognitionConfig, "http://localhost/models/asr", true, cts.Token);
        await synthesisRouter.StartDownloadAsync(synthesisConfig, "http://localhost/models/tts", true, cts.Token);

        Assert.Same(recognitionConfig, recognitionProvider.DownloadConfig);
        Assert.Equal("http://localhost/models/asr", recognitionProvider.ModelDownloadUrl);
        Assert.Equal(cts.Token, recognitionProvider.DownloadCancellationToken);
        Assert.Same(synthesisConfig, synthesisProvider.DownloadConfig);
        Assert.Equal("http://localhost/models/tts", synthesisProvider.ModelDownloadUrl);
        Assert.Equal(cts.Token, synthesisProvider.DownloadCancellationToken);
    }

    [Fact]
    public void GetDownloadModelPath_WithMissingProviders_ReturnsNull()
    {
        var catalog = new TestSpeechProviderCatalog();
        var config = new SpeechProviderConfig { Type = "missing" };

        Assert.Null(new SpeechRecognitionRouter(catalog).GetDownloadModelPath(config, requireLocalEndpoint: true));
        Assert.Null(new SpeechSynthesisRouter(catalog).GetDownloadModelPath(config, requireLocalEndpoint: true));
    }

    private sealed class TestSpeechProviderCatalog(
        IReadOnlyList<ISpeechRecognitionProvider>? recognition = null,
        IReadOnlyList<ISpeechSynthesisProvider>? synthesis = null) : ISpeechProviderCatalog
    {
        public IReadOnlyList<ISpeechRecognitionProvider> Recognition { get; } = recognition ?? [];

        public IReadOnlyList<ISpeechSynthesisProvider> Synthesis { get; } = synthesis ?? [];
    }

    private sealed class RecordingRecognitionProvider(string type, string? defaultEndpoint) : ISpeechRecognitionProvider
    {
        public string Type { get; } = type;
        public string? DefaultModel => null;

        public string? DefaultEndpoint { get; } = defaultEndpoint;

        public string? ModelPath => null;

        public SpeechProviderConfig? Config { get; private set; }

        public string? ApiKey { get; private set; }

        public byte[]? WavBytes { get; private set; }

        public string? Language { get; private set; }

        public int InvocationCount { get; private set; }


        public SpeechProviderConfig? DownloadConfig { get; private set; }

        public string? ModelDownloadUrl { get; private set; }

        public CancellationToken DownloadCancellationToken { get; private set; }

        public Task<SpeechTranscription> TranscribeAsync(
            SpeechProviderConfig config,
            string? apiKey,
            byte[] wavBytes,
            string language = "auto",
            CancellationToken ct = default)
        {
            InvocationCount++;
            Config = config;
            ApiKey = apiKey;
            WavBytes = wavBytes;
            Language = language;
            return Task.FromResult(new SpeechTranscription { Text = "transcribed" });
        }

        public Task<bool> IsAvailableAsync(SpeechProviderConfig config, string? apiKey, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task StartDownloadAsync(SpeechProviderConfig config, string? modelDownloadUrl, CancellationToken ct = default)
        {
            DownloadConfig = config;
            ModelDownloadUrl = modelDownloadUrl;
            DownloadCancellationToken = ct;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSynthesisProvider(string type, string? defaultEndpoint) : ISpeechSynthesisProvider
    {
        public TaskCompletionSource? StartGate { get; set; }
        public int StopCount { get; private set; }
        public string Type { get; } = type;
        public string? DefaultModel => null;
        public string? DefaultVoiceId => null;

        public string? DefaultEndpoint { get; } = defaultEndpoint;

        public string? ModelPath => null;

        public SpeechSynthesisResponse Response { get; } = new();

        public SpeechSynthesisVoice Voice { get; } = new("custom", "Custom", "English", "Female");

        public SpeechProviderConfig? Config { get; private set; }

        public string? ApiKey { get; private set; }

        public string? UserId { get; private set; }

        public SpeechSynthesisRequest? Request { get; private set; }

        public int InvocationCount { get; private set; }

        public SpeechProviderConfig? DownloadConfig { get; private set; }

        public string? ModelDownloadUrl { get; private set; }

        public CancellationToken DownloadCancellationToken { get; private set; }

        public Task StartSessionAsync(
            string userId,
            SpeechProviderConfig config,
            string? apiKey,
            string? voiceId,
            CancellationToken ct = default) => StartGate?.Task ?? Task.CompletedTask;

        public Task StopSessionAsync(string userId, SpeechProviderConfig config, CancellationToken ct = default)
        {
            StopCount++;
            return Task.CompletedTask;
        }

        public Task<bool> IsAvailableAsync(SpeechProviderConfig config, string? apiKey, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<IReadOnlyList<SpeechSynthesisVoice>> GetAvailableVoicesAsync(
            SpeechProviderConfig config,
            string? apiKey,
            CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SpeechSynthesisVoice>>([Voice]);

        public Task<SpeechSynthesisResponse> SynthesizeAsync(
            string userId,
            SpeechProviderConfig config,
            string? apiKey,
            SpeechSynthesisRequest request,
            CancellationToken ct = default)
        {
            InvocationCount++;
            UserId = userId;
            Config = config;
            ApiKey = apiKey;
            Request = request;
            return Task.FromResult(Response);
        }

        public Task StartDownloadAsync(SpeechProviderConfig config, string? modelDownloadUrl, CancellationToken ct = default)
        {
            DownloadConfig = config;
            ModelDownloadUrl = modelDownloadUrl;
            DownloadCancellationToken = ct;
            return Task.CompletedTask;
        }
    }
}
