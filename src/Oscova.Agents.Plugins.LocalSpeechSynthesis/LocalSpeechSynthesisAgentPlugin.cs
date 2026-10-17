using Oscova.Agents.Speech;
using KokoroSharp;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Oscova.Agents.Plugins.LocalSpeechSynthesis;

/// <summary>Registers the local speech synthesis provider.</summary>
public sealed class LocalSpeechSynthesisAgentPlugin : IAgentPlugin
{
#if LOCAL_SPEECH_CUDA
    internal const string Backend = "CUDA";
#else
    internal const string Backend = "CPU";
#endif

    /// <summary>Adds the local Kokoro provider.</summary>
    public void Configure(AgentPluginBuilder builder) => builder.UseResources<PluginText>().AddSpeechSynthesisProvider(CreateSynthesisProvider)
        .AddSettingsPanel(new("synthesis", PluginSettingsSlots.SpeechSynthesis, SpeechProviderKeys.Local,
            typeof(SpeechSynthesisSettingsComponent), context => context, PluginSettingsAccess.CurrentUser));

    // The SDK emits these values from the same properties used by package references.
    private static string GetDependencyVersion(string key) =>
        typeof(LocalSpeechSynthesisAgentPlugin).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == key).Value!;

    private static KokoroSpeechSynthesisProvider CreateSynthesisProvider(AgentPluginContext context)
    {
        var contentRoot = Path.Combine(
            context.PluginDirectory,
            "packages",
            "kokorosharp",
            GetDependencyVersion("KokoroVersion"),
            "content");

        // Kokoro recursively loads both voice folders materialized from its dependency package.
        KokoroVoiceManager.LoadVoicesFromPath(contentRoot);
        return new KokoroSpeechSynthesisProvider(
            Path.Combine(context.ModelStorageDirectory, "kokoro.onnx"),
            context.EnsureDownloadAsync,
            context.LoggerFactory.CreateLogger<KokoroSpeechSynthesisProvider>());
    }
}
