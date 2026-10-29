using Microsoft.Extensions.AI;
using Oscova.Agents.Llm;

namespace Oscova.Agents.Core.Settings;

public static class ModelProviderConfigPresets
{
    public const string AnthropicDefaultModelId = "claude-haiku-5-5";
    public const int AnthropicDefaultMaxOutputTokens = 8192;

    public static IEnumerable<ModelProviderConfig> GetPresets()
    {
        yield return new ModelProviderConfig
        {
            Name = "Ollama",
            Type = ModelProviderKeys.Ollama,
            BaseUrl = "http://localhost:11434/",
            ModelId = "qwen3.5:4b",
            ContextWindowSize = 40960,
            ChatOptions = new ChatOptions { Temperature = 0.7f }
        };

        yield return new ModelProviderConfig
        {
            Name = "OpenAI",
            Type = ModelProviderKeys.OpenAI,
            BaseUrl = string.Empty,
            ModelId = string.Empty,
            SupportsImages = true,
            ContextWindowSize = 128000,
            ChatOptions = new ChatOptions { Temperature = 0.7f }
        };

        yield return new ModelProviderConfig
        {
            Name = "DeepSeek",
            Type = ModelProviderKeys.DeepSeek,
            BaseUrl = "https://api.deepseek.com/",
            ModelId = "deepseek-flash",
            SupportsImages = true,
            SupportsThinking = true,
            ContextWindowSize = 1048576,
            ChatOptions = new ChatOptions()
        };

        yield return new ModelProviderConfig
        {
            Name = "Groq",
            Type = ModelProviderKeys.Groq,
            BaseUrl = "https://api.groq.com/openai/v1/",
            ModelId = string.Empty,
            ContextWindowSize = 128000,
            ChatOptions = new ChatOptions { Temperature = 0.7f }
        };

        yield return new ModelProviderConfig
        {
            Name = "OpenRouter",
            Type = ModelProviderKeys.OpenRouter,
            BaseUrl = "https://openrouter.ai/api/v1/",
            ModelId = string.Empty,
            SupportsImages = true,
            ContextWindowSize = 128000,
            ChatOptions = new ChatOptions { Temperature = 0.7f }
        };

        yield return new ModelProviderConfig
        {
            Name = "NVIDIA NIM",
            Type = ModelProviderKeys.NvidiaNim,
            BaseUrl = "https://integrate.api.nvidia.com/v1/",
            ModelId = "nvidia/nemotron-3.5-lightning-30b-a3b",
            ContextWindowSize = 128000,
            ChatOptions = new ChatOptions { Temperature = 0.7f }
        };

        yield return new ModelProviderConfig
        {
            Name = "OpenAI Compatible",
            Type = ModelProviderKeys.OpenAICompatible,
            BaseUrl = "http://localhost:1234/v1/",
            ModelId = string.Empty,
            ContextWindowSize = 128000,
            ChatOptions = new ChatOptions { Temperature = 0.7f }
        };

        yield return new ModelProviderConfig
        {
            Name = "Google Gemini",
            Type = ModelProviderKeys.Google,
            BaseUrl = string.Empty,
            ModelId = "gemini-2.0-flash",
            SupportsImages = true,
            SupportsThinking = true,
            ContextWindowSize = 128000,
            ChatOptions = new ChatOptions { Temperature = 0.7f }
        };

        yield return new ModelProviderConfig
        {
            Name = "Anthropic",
            Type = ModelProviderKeys.Anthropic,
            BaseUrl = string.Empty,
            ModelId = AnthropicDefaultModelId,
            SupportsImages = true,
            ContextWindowSize = 200000,
            ChatOptions = new ChatOptions { MaxOutputTokens = AnthropicDefaultMaxOutputTokens }
        };

        yield return new ModelProviderConfig
        {
            Name = "Local (LLamaSharp)",
            Type = ModelProviderKeys.Local,
            BaseUrl = "",
            ModelId = "",
            ContextWindowSize = 4096,
            ChatOptions = new ChatOptions { Temperature = 0.7f }
        };

        yield return new ModelProviderConfig
        {
            Name = "LM Studio",
            Type = ModelProviderKeys.OpenAICompatible,
            BaseUrl = "http://localhost:1234/v1/",
            ModelId = string.Empty,
            ContextWindowSize = 8192,
            ChatOptions = new ChatOptions { Temperature = 0.7f }
        };
    }
}

