using System.Collections.Generic;

namespace Oscova.Agents.Speech;

/// <summary>
/// Represents the recognized text and optional segment breakdown.
/// </summary>
public class SpeechTranscription
{
    /// <summary>
    /// Gets or sets the recognized text.
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the recognized text segments.
    /// </summary>
    public IList<string> Segments { get; set; } = [];
}
