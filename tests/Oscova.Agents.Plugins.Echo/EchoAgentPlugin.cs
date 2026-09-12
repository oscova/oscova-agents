using System.ComponentModel;
using Humanizer;
using Microsoft.Extensions.AI;
using Oscova.Agents.Credentials;
using Oscova.Agents.Llm;
using Oscova.Agents.Speech;
using Oscova.Agents.Speech.Contracts;

namespace Oscova.Agents.Plugins.Echo;

/// <summary>
/// Sample plugin entry point that registers the echo toolset and serves as the canonical Oscova plugin example.
/// </summary>
public sealed class EchoAgentPlugin : IAgentPlugin
{
    /// <summary>
    /// Registers the echo toolset contribution.
    /// </summary>
    public void Configure(AgentPluginBuilder builder)
    {
        builder.AddToolset(_ => new EchoToolset())
            .AddModelProvider(_ => new EchoModelProvider())
            .AddSpeechSynthesisProvider(_ => new EchoSpeechSynthesisProvider());
    }
}

/// <summary>
/// Small sample toolset that proves plugin tools flow through standard reflection.
/// </summary>
[CredentialSlot(
    Key = "test-echo",
    Label = "Test Echo",
    Description = "Credential used by the plugin loader test assembly.")]
public sealed class EchoToolset : ICrewToolset
{
    /// <summary>
    /// Returns the supplied text unchanged.
    /// </summary>
    [DisplayName("test_echo")]
    [Description("Returns the supplied text unchanged.")]
    public static string Echo(string text)
    {
        return text;
    }

    /// <summary>Returns the plural form produced by the plugin's private dependency.</summary>
    [DisplayName("test_pluralize")]
    [Description("Returns the plural form of an English word.")]
    public static string Pluralize(string text)
    {
        return text.Pluralize();
    }
}

/// <summary>
/// Sample plugin model provider used to verify provider contributions without network calls.
/// </summary>
[CredentialSlot(
    Key = "echo",
    Label = "Echo API Key",
    Description = "Credential for the echo model provider.")]
public sealed class EchoModelProvider : IModelProvider
{
    public string Type => "echo";

    public string? DefaultEndpoint => "https://echo.invalid/";

    public bool? DefaultIsLocalEndpoint => null;

    public string? DefaultModel => "echo-1";

    public bool SupportsEmbedding => false;

    public ValueTask<IChatClient> CreateClientAsync(
        ModelProviderConfig config,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("The echo model provider is a catalog test double.");
    }

    public ValueTask<IEmbeddingGenerator<string, Embedding<float>>> CreateEmbeddingGeneratorAsync(
        ModelProviderConfig config,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("The echo model provider does not support embeddings.");
    }

    public Task<IReadOnlyList<ModelDescriptor>> GetAvailableModelsAsync(
        ModelProviderConfig config,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<ModelDescriptor>>([new ModelDescriptor { Id = "echo-1" }]);
    }

    public Task<(bool Success, string Message)> TestAsync(
        ModelProviderConfig config,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult((true, "Echo model provider is available."));
    }
}

/// <summary>
/// Sample plugin speech synthesis provider used to verify speech provider contributions.
/// </summary>
public sealed class EchoSpeechSynthesisProvider : ISpeechSynthesisProvider
{
    public string Type => "echo-tts";

    public string? DefaultEndpoint => null;

    public string? ModelPath => null;

    public Task StartSessionAsync(string userId, SpeechProviderConfig config, string? apiKey, string? voiceId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task StopSessionAsync(string userId, SpeechProviderConfig config, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<bool> IsAvailableAsync(SpeechProviderConfig config, string? apiKey, CancellationToken ct = default)
        => Task.FromResult(true);

    public Task<IReadOnlyList<SpeechSynthesisVoice>> GetAvailableVoicesAsync(SpeechProviderConfig config, string? apiKey, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SpeechSynthesisVoice>>([new("echo", "Echo")]);

    public Task<SpeechSynthesisResponse> SynthesizeAsync(
        string userId,
        SpeechProviderConfig config,
        string? apiKey,
        SpeechSynthesisRequest request,
        CancellationToken ct = default)
    {
        return Task.FromResult(new SpeechSynthesisResponse
        {
            Audio = [],
            SampleRate = 24000,
            Channels = 1,
            BitsPerSample = 16,
            TotalSamples = 0,
            VoiceId = request.VoiceId ?? "echo"
        });
    }

    public Task StartDownloadAsync(SpeechProviderConfig config, string? modelDownloadUrl, CancellationToken ct = default)
        => Task.CompletedTask;
}
