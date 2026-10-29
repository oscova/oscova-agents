namespace Oscova.Agents.Core.ApiContracts.Speech;

/// <summary>Describes one synthesis voice returned by the Host API.</summary>
public sealed class SpeechVoiceSummary
{
    /// <summary>Gets or sets the stable provider voice identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the user-facing voice name.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional language or accent.</summary>
    public string? Language { get; set; }

    /// <summary>Gets or sets the optional provider-reported gender.</summary>
    public string? Gender { get; set; }
}
