namespace Oscova.Agents.Llm;

/// <summary>
/// Well-known built-in model provider keys.
/// </summary>
public static class ModelProviderKeys
{
    /// <summary>Uses Ollama-hosted models.</summary>
    public const string Ollama = "Ollama";

    /// <summary>Uses OpenAI-hosted models.</summary>
    public const string OpenAI = "OpenAI";

    /// <summary>Uses Groq-hosted models.</summary>
    public const string Groq = "Groq";

    /// <summary>Uses an OpenAI-compatible endpoint.</summary>
    public const string OpenAICompatible = "OpenAICompatible";

    /// <summary>Uses a local model provider.</summary>
    public const string Local = "Local";

    /// <summary>Uses Google-hosted models.</summary>
    public const string Google = "Google";

    /// <summary>Uses Anthropic-hosted models.</summary>
    public const string Anthropic = "Anthropic";

    /// <summary>Uses OpenRouter-hosted models.</summary>
    public const string OpenRouter = "OpenRouter";

    /// <summary>Uses NVIDIA NIM-hosted OpenAI-compatible chat models.</summary>
    public const string NvidiaNim = "NvidiaNim";
}
