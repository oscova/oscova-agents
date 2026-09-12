using System;

namespace Oscova.Agents.Speech;

/// <summary>
/// Stores one speech provider configuration for recognition or synthesis.
/// </summary>
public sealed class SpeechProviderConfig
{
    /// <summary>
    /// Gets or sets the provider identifier.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Gets or sets the display name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the provider type.
    /// </summary>
    public string Type { get; set; } = SpeechProviderKeys.Local;

    /// <summary>
    /// Gets or sets the optional service base URL.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Gets or sets the model identifier.
    /// </summary>
    public string? ModelId { get; set; }

    /// <summary>
    /// Gets or sets the credential lookup key.
    /// </summary>
    public string? CredentialKey { get; set; }

    /// <summary>
    /// Gets or sets the preferred voice identifier.
    /// </summary>
    public string? VoiceId { get; set; }

    /// <summary>
    /// Creates a copy of this provider configuration.
    /// </summary>
    public SpeechProviderConfig Clone()
    {
        return new SpeechProviderConfig
        {
            Id = Id,
            Name = Name,
            Type = Type,
            BaseUrl = BaseUrl,
            ModelId = ModelId,
            CredentialKey = CredentialKey,
            VoiceId = VoiceId
        };
    }

    /// <summary>
    /// Compares this configuration to another configuration instance.
    /// </summary>
    public bool EqualsConfiguration(SpeechProviderConfig other)
    {
        return Id == other.Id
            && string.Equals(Name, other.Name, StringComparison.Ordinal)
            && string.Equals(Type, other.Type, StringComparison.Ordinal)
            && string.Equals(BaseUrl, other.BaseUrl, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ModelId, other.ModelId, StringComparison.Ordinal)
            && string.Equals(CredentialKey, other.CredentialKey, StringComparison.Ordinal)
            && string.Equals(VoiceId, other.VoiceId, StringComparison.Ordinal);
    }
}
