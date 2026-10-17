using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oscova.Agents.Policies;

namespace Oscova.Agents.Speech;

/// <summary>
/// Routes speech recognition operations to an explicitly configured provider.
/// </summary>
public sealed class SpeechRecognitionRouter(ISpeechProviderCatalog speechProviderCatalog)
{
    private readonly IReadOnlyDictionary<string, ISpeechRecognitionProvider> _providers =
        speechProviderCatalog.Recognition.GroupBy(provider => provider.Type).ToDictionary(group => group.Key, group => group.First());

    /// <summary>Transcribes WAV audio through the configured provider.</summary>
    public async Task<SpeechTranscription> TranscribeAsync(
        SpeechProviderConfig config,
        string? apiKey,
        byte[] wavBytes,
        bool requireLocalEndpoint,
        string language = "auto",
        CancellationToken ct = default)
    {
        var provider = ResolveProvider(config, requireLocalEndpoint);
        return await provider.TranscribeAsync(config, apiKey, wavBytes, language, ct).ConfigureAwait(false);
    }

    /// <summary>Determines whether the configured provider is available.</summary>
    public async Task<bool> IsAvailableAsync(
        SpeechProviderConfig config,
        string? apiKey,
        bool requireLocalEndpoint,
        CancellationToken ct = default)
    {
        if (!_providers.ContainsKey(config.Type))
        {
            return false;
        }

        var provider = ResolveProvider(config, requireLocalEndpoint);
        return await provider.IsAvailableAsync(config, apiKey, ct).ConfigureAwait(false);
    }

    /// <summary>Starts downloading model assets for the configured provider.</summary>
    public async Task StartDownloadAsync(
        SpeechProviderConfig config,
        string? modelDownloadUrl,
        bool requireLocalEndpoint,
        CancellationToken ct = default)
    {
        var provider = ResolveProvider(config, requireLocalEndpoint);
        if (requireLocalEndpoint && EgressClassifier.ClassifySpeech(modelDownloadUrl, null) == EgressClass.Cloud)
            throw new InvalidOperationException("Offline mode is on: this speech model download uses a non-local endpoint and is blocked.");

        await provider.StartDownloadAsync(config, modelDownloadUrl, ct).ConfigureAwait(false);
    }

    /// <summary>Gets the configured provider's local model path.</summary>
    public string? GetDownloadModelPath(SpeechProviderConfig config, bool requireLocalEndpoint)
    {
        if (!_providers.ContainsKey(config.Type))
        {
            return null;
        }

        var provider = ResolveProvider(config, requireLocalEndpoint);
        return provider.ModelPath;
    }

    private ISpeechRecognitionProvider ResolveProvider(SpeechProviderConfig config, bool requireLocalEndpoint)
    {
        if (!_providers.TryGetValue(config.Type, out var provider))
        {
            throw new InvalidOperationException($"No speech recognition provider is registered for {config.Type}.");
        }

        if (requireLocalEndpoint &&
            EgressClassifier.ClassifySpeech(config.BaseUrl, provider.DefaultEndpoint) == EgressClass.Cloud)
        {
            throw new InvalidOperationException("Offline mode is on: this speech provider uses a non-local endpoint and is blocked.");
        }

        return provider;
    }
}
