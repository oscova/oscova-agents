using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Oscova.Agents.Speech.Contracts;

namespace Oscova.Agents.Speech;

/// <summary>
/// Provider-specific speech synthesis implementation used by the speech routing service.
/// </summary>
public interface ISpeechSynthesisProvider
{
    /// <summary>
    /// Gets the provider type handled by this implementation.
    /// </summary>
    string Type { get; }

    /// <summary>
    /// Gets the default API endpoint, or null for in-process providers.
    /// </summary>
    string? DefaultEndpoint { get; }

    /// <summary>
    /// Gets the local model path when applicable.
    /// </summary>
    string? ModelPath { get; }

    /// <summary>
    /// Starts a synthesis session for the supplied user.
    /// </summary>
    Task StartSessionAsync(string userId, SpeechProviderConfig config, string? apiKey, string? voiceId, CancellationToken ct = default);

    /// <summary>
    /// Stops a synthesis session for the supplied user.
    /// </summary>
    Task StopSessionAsync(string userId, SpeechProviderConfig config, CancellationToken ct = default);

    /// <summary>
    /// Determines whether the provider is currently available.
    /// </summary>
    Task<bool> IsAvailableAsync(SpeechProviderConfig config, string? apiKey, CancellationToken ct = default);

    /// <summary>
    /// Returns the available voices for the supplied configuration.
    /// </summary>
    Task<IReadOnlyList<SpeechSynthesisVoice>> GetAvailableVoicesAsync(SpeechProviderConfig config, string? apiKey, CancellationToken ct = default);

    /// <summary>
    /// Synthesizes speech for the supplied request.
    /// </summary>
    Task<SpeechSynthesisResponse> SynthesizeAsync(
        string userId,
        SpeechProviderConfig config,
        string? apiKey,
        SpeechSynthesisRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Starts downloading any provider model assets if needed.
    /// </summary>
    Task StartDownloadAsync(SpeechProviderConfig config, string? modelDownloadUrl, CancellationToken ct = default);
}
