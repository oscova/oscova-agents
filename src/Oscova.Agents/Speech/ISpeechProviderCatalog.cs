using System.Collections.Generic;

namespace Oscova.Agents.Speech;

/// <summary>
/// Provides the merged set of built-in and plugin-contributed speech providers.
/// </summary>
public interface ISpeechProviderCatalog
{
    /// <summary>
    /// Gets every available speech recognition provider.
    /// </summary>
    IReadOnlyList<ISpeechRecognitionProvider> Recognition { get; }

    /// <summary>
    /// Gets every available speech synthesis provider.
    /// </summary>
    IReadOnlyList<ISpeechSynthesisProvider> Synthesis { get; }
}
