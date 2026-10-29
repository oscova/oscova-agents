namespace Oscova.Agents.Core.ApiContracts.Speech;

/// <summary>
/// Carries an unsaved speech synthesis provider draft for voice discovery.
/// </summary>
public sealed class SpeechVoicePreviewRequest
{
    /// <summary>
    /// Gets or sets the provider type to query.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional provider endpoint.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Gets or sets the provider model identifier.
    /// </summary>
    public string? ModelId { get; set; }

    /// <summary>
    /// Gets or sets the credential key used by hosted providers.
    /// </summary>
    public string? CredentialKey { get; set; }

    /// <summary>
    /// Gets or sets the currently selected draft voice.
    /// </summary>
    public string? VoiceId { get; set; }
}
