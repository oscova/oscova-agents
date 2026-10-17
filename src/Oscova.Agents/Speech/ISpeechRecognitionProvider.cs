using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Speech;

/// <summary>
/// Provider-specific speech recognition implementation used by the speech routing service.
/// </summary>
public interface ISpeechRecognitionProvider
{
    /// <summary>
    /// Gets the provider type handled by this implementation.
    /// </summary>
    string Type { get; }

    /// <summary>Gets the model used when configuration supplies no model.</summary>
    string? DefaultModel { get; }

    /// <summary>
    /// Gets the default API endpoint, or null for in-process providers.
    /// </summary>
    string? DefaultEndpoint { get; }

    /// <summary>
    /// Gets the local model path when applicable.
    /// </summary>
    string? ModelPath { get; }

    /// <summary>
    /// Transcribes WAV audio using the supplied provider configuration.
    /// </summary>
    Task<SpeechTranscription> TranscribeAsync(
        SpeechProviderConfig config,
        string? apiKey,
        byte[] wavBytes,
        string language = "auto",
        CancellationToken ct = default);

    /// <summary>
    /// Determines whether the provider is currently available.
    /// </summary>
    Task<bool> IsAvailableAsync(SpeechProviderConfig config, string? apiKey, CancellationToken ct = default);

    /// <summary>
    /// Starts downloading any provider model assets if needed.
    /// </summary>
    Task StartDownloadAsync(SpeechProviderConfig config, string? modelDownloadUrl, CancellationToken ct = default);
}
