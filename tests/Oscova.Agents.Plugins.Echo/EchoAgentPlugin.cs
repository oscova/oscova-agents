using System.ComponentModel;
using System.Text;
using Oscova.Agents.Workspace;
using Oscova.Agents.Channels;
using Humanizer;
using Microsoft.Extensions.AI;
using Oscova.Agents.Credentials;
using Oscova.Agents.Llm;
using Oscova.Agents.Speech;
using Oscova.Agents.Speech.Contracts;
using Oscova.Agents.Tasks;

namespace Oscova.Agents.Plugins.Echo;

/// <summary>
/// Sample plugin entry point that registers the echo toolset and serves as the canonical Oscova plugin example.
/// </summary>
public sealed class EchoAgentPlugin : IAgentPlugin
{
    /// <summary>Registers Echo's task, tool, provider and channel contributions.</summary>
    public void Configure(AgentPluginBuilder builder)
    {
        builder.UseResources<PluginText>().AddTask(
            "record-run",
            _ => new AgentTaskDefinition
            {
                Name = "Record plugin task run",
                Instruction = "Record a task execution marker.",
                Schedule = new TaskSchedule { Kind = TaskScheduleKind.Interval, IntervalMinutes = 15 },
                IsEnabled = false
            },
            deterministicHandlerFactory: context => new EchoTaskHandler(
                context.UserDataDirectory ?? throw new InvalidOperationException("Echo task storage requires a user context."),
                context.Services.Encryption))
            .AddToolset(_ => new EchoToolset())
            .AddToolset(context => new EchoProjectToolset(context.GetAllowedDirectories), supportsNodeExecution: true)
            .AddModelProvider(_ => new EchoModelProvider())
            .AddSpeechSynthesisProvider(_ => new EchoSpeechSynthesisProvider())
            .AddCredential(new CredentialDeclaration { Key = "echo-channel", Label = "Echo channel", Fields = [new() { Name = "token", Label = "Token" }] })
            .AddChannel(new EchoChannel())
            .AddSettingsPanel(new("settings", PluginSettingsSlots.Page, null,
                typeof(EchoChannelSettingsComponent), context => context, PluginSettingsAccess.CurrentUser,
                Help: new PluginHelpTopic("settings", "Oscova.Agents.Plugins.Echo.Help.settings.md"))
            {
                Page = new("workspace", "Echo", "Plugin-owned settings.", "Channel.Title", "Channel.Description", "Oscova.Agents.Plugins.Echo.Icons.echo.svg"),
                RelatedHelp = [new PluginHelpTopic("other", "Oscova.Agents.Plugins.Echo.Help.other.md")]
            });
    }
}

/// <summary>Proves project execution and private dependency loading on the selected device.</summary>
public sealed class EchoProjectToolset(Func<IReadOnlyList<AllowedDirectory>> directories) : IAgentToolset
{
    /// <summary>Writes a bounded marker through the same authorized file boundary as built-in project tools.</summary>
    [AgentTool]
    [Description("Writes device identity and a pluralized word into a project marker file.\npath must be inside an authorized project directory.")]
    public string WriteProjectMarker(
        [Description("Absolute path inside an authorized project directory.")] string path,
        CancellationToken ct = default)
    {
        var files = new WorkspaceFileAccess(directories);
        var content = Environment.MachineName + ":" + "plugin".Pluralize();
        ct.ThrowIfCancellationRequested();
        files.Write(path, stream => stream.Write(Encoding.UTF8.GetBytes(content)));
        return content;
    }
}

/// <summary>Registers metadata for the plugin loader's channel probe.</summary>
public sealed class EchoChannel : IAgentChannel
{
    public ChannelDefinition Definition { get; } = new()
    {
        Id = "echo", Title = "Echo", TitleKey = "Channel.Title", Description = "Loader registration probe.",
        DescriptionKey = "Channel.Description", IconResourceName = "Oscova.Agents.Plugins.Echo.Icons.echo.svg",
        Help = new PluginHelpTopic("settings", "Oscova.Agents.Plugins.Echo.Help.settings.md"),
        RelatedHelp = [new PluginHelpTopic("other", "Oscova.Agents.Plugins.Echo.Help.other.md")],
        CredentialKeys = ["echo-channel"]
    };

    public Task<ChannelRuntimeConfiguration> ReadConfigurationAsync(AgentPluginContext context, bool requireComplete, CancellationToken ct = default)
        => Task.FromResult(new ChannelRuntimeConfiguration(false, null,
            _ => throw new NotSupportedException("This test contribution registers metadata only.")));

}

/// <summary>Writes an encrypted marker proving that the loaded task handler executed.</summary>
public sealed class EchoTaskHandler(string outputRoot, IPluginEncryptionService encryption) : IDeterministicTaskHandler
{
    /// <summary>Records the task name under the current user's data root.</summary>
    public async Task ExecuteAsync(AgentTaskDefinition task, CancellationToken ct = default)
    {
        var ciphertext = encryption.Encrypt(task.Name);
        Directory.CreateDirectory(outputRoot);
        await File.WriteAllTextAsync(
            Path.Combine(outputRoot, $"task-{task.UserId}-{task.Id}.txt.enc"),
            ciphertext,
            ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Small sample toolset that proves plugin tools flow through standard reflection.
/// </summary>
[CredentialSlot(
    Key = "test-echo",
    Label = "Test Echo",
    Description = "Credential used by the plugin loader test assembly.")]
public sealed class EchoToolset : IAgentToolset
{
    /// <summary>
    /// Returns the supplied text unchanged.
    /// </summary>
    [DisplayName("test_echo")]
    [AgentTool]
    [Description("Returns the supplied text unchanged.")]
    public static string Echo(string text)
    {
        return text;
    }

    /// <summary>Returns the plural form produced by the plugin's private dependency.</summary>
    [DisplayName("test_pluralize")]
    [AgentTool]
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
    public string? DefaultModel => null;
    public string? DefaultVoiceId => null;

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
