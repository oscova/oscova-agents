namespace Oscova.Agents.Speech;

/// <summary>
/// Well-known built-in speech provider keys.
/// </summary>
public static class SpeechProviderKeys
{
    /// <summary>Uses a local speech runtime.</summary>
    public const string Local = "Local";

    /// <summary>Uses OpenAI-hosted speech APIs.</summary>
    public const string OpenAI = "OpenAI";
}
