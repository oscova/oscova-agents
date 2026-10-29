using Oscova.Agents.Infrastructure.Plugins;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Llm;
using Oscova.Agents.Speech;
using Oscova.Agents.Speech.Contracts;

namespace Oscova.Agents.Infrastructure.Speech;

/// <summary>
/// Merges built-in and plugin-contributed speech providers for runtime resolution.
/// </summary>
public sealed class SpeechProviderCatalog : ISpeechProviderCatalog
{
    internal SpeechProviderCatalog(
        IEnumerable<ISpeechRecognitionProvider> recognitionBuiltIns,
        IEnumerable<ISpeechSynthesisProvider> synthesisBuiltIns,
        PluginProviderActivation pluginProviders,
        ModelProviderActivity activity, RunContextAccessor runs)
    {
        Recognition = recognitionBuiltIns.Concat(pluginProviders.SpeechRecognitionProviders)
            .Select(provider => (ISpeechRecognitionProvider)new RecognitionProvider(provider, activity, runs)).ToArray();
        Synthesis = synthesisBuiltIns.Concat(pluginProviders.SpeechSynthesisProviders)
            .Select(provider => (ISpeechSynthesisProvider)new SynthesisProvider(provider, activity, runs)).ToArray();
    }

    public IReadOnlyList<ISpeechRecognitionProvider> Recognition { get; }

    public IReadOnlyList<ISpeechSynthesisProvider> Synthesis { get; }

    // The dispatch entrypoint supplies the consumer. Retained speech sessions and downloads are not requests.
    private static IDisposable? Observe(ModelProviderActivity activity, RunContextAccessor runs, Guid providerId)
        => runs.Current is { } run ? activity.Begin(run.UserId, providerId, run.ExecutionKind) : null;

    private sealed class RecognitionProvider(ISpeechRecognitionProvider inner, ModelProviderActivity activity,
        RunContextAccessor runs) : ISpeechRecognitionProvider
    {
        public string Type => inner.Type;
        public string? DefaultModel => inner.DefaultModel;
        public string? DefaultEndpoint => inner.DefaultEndpoint;
        public string? ModelPath => inner.ModelPath;

        public async Task<SpeechTranscription> TranscribeAsync(SpeechProviderConfig config, string? apiKey,
            byte[] wavBytes, string language = "auto", CancellationToken ct = default)
        {
            using var request = Observe(activity, runs, config.Id);
            return await inner.TranscribeAsync(config, apiKey, wavBytes, language, ct).ConfigureAwait(false);
        }

        public Task<bool> IsAvailableAsync(SpeechProviderConfig config, string? apiKey, CancellationToken ct = default)
            => inner.IsAvailableAsync(config, apiKey, ct);

        public Task StartDownloadAsync(SpeechProviderConfig config, string? modelDownloadUrl, CancellationToken ct = default)
            => inner.StartDownloadAsync(config, modelDownloadUrl, ct);
    }

    private sealed class SynthesisProvider(ISpeechSynthesisProvider inner, ModelProviderActivity activity,
        RunContextAccessor runs) : ISpeechSynthesisProvider
    {
        public string Type => inner.Type;
        public string? DefaultModel => inner.DefaultModel;
        public string? DefaultVoiceId => inner.DefaultVoiceId;
        public string? DefaultEndpoint => inner.DefaultEndpoint;
        public string? ModelPath => inner.ModelPath;

        public async Task StartSessionAsync(string userId, SpeechProviderConfig config, string? apiKey,
            string? voiceId, CancellationToken ct = default)
        {
            using var request = Observe(activity, runs, config.Id);
            await inner.StartSessionAsync(userId, config, apiKey, voiceId, ct).ConfigureAwait(false);
        }

        public async Task<SpeechSynthesisResponse> SynthesizeAsync(string userId, SpeechProviderConfig config,
            string? apiKey, SpeechSynthesisRequest request, CancellationToken ct = default)
        {
            using var inference = Observe(activity, runs, config.Id);
            return await inner.SynthesizeAsync(userId, config, apiKey, request, ct).ConfigureAwait(false);
        }

        public Task StopSessionAsync(string userId, SpeechProviderConfig config, CancellationToken ct = default)
            => inner.StopSessionAsync(userId, config, ct);

        public Task<bool> IsAvailableAsync(SpeechProviderConfig config, string? apiKey, CancellationToken ct = default)
            => inner.IsAvailableAsync(config, apiKey, ct);

        public Task<IReadOnlyList<SpeechSynthesisVoice>> GetAvailableVoicesAsync(SpeechProviderConfig config,
            string? apiKey, CancellationToken ct = default) => inner.GetAvailableVoicesAsync(config, apiKey, ct);

        public Task StartDownloadAsync(SpeechProviderConfig config, string? modelDownloadUrl, CancellationToken ct = default)
            => inner.StartDownloadAsync(config, modelDownloadUrl, ct);
    }

}
