using KokoroSharp;
using Microsoft.Extensions.Logging;
using Whisper.net.LibraryLoader;

namespace Oscova.Agents.Plugins.LocalSpeech;

/// <summary>Registers Oscova's local speech recognition and synthesis providers.</summary>
public sealed class LocalSpeechAgentPlugin : IAgentPlugin
{
    /// <summary>Adds the local Whisper and Kokoro providers.</summary>
    public void Configure(AgentPluginBuilder builder)
    {
        builder
            .AddSpeechRecognitionProvider(CreateRecognitionProvider)
            .AddSpeechSynthesisProvider(CreateSynthesisProvider);
    }

    private static WhisperSpeechRecognitionProvider CreateRecognitionProvider(AgentPluginContext context)
    {
        // Whisper's probe cannot see the plugin's standard RID tree, so let the plugin load context resolve it.
        RuntimeOptions.LoadedLibrary = RuntimeLibrary.Cpu;
        return new WhisperSpeechRecognitionProvider(
            Path.Combine(context.ModelStorageDirectory, "ggml-base.bin"),
            context.EnsureDownloadAsync,
            context.LoggerFactory.CreateLogger<WhisperSpeechRecognitionProvider>());
    }

    private static KokoroSpeechSynthesisProvider CreateSynthesisProvider(AgentPluginContext context)
    {
        var contentRoot = Path.Combine(
            context.PluginDirectory,
            "packages",
            "kokorosharp",
            "0.8.4",
            "content");

        // Kokoro recursively loads both voice folders materialized from its dependency package.
        KokoroVoiceManager.LoadVoicesFromPath(contentRoot);
        return new KokoroSpeechSynthesisProvider(
            Path.Combine(context.ModelStorageDirectory, "kokoro.onnx"),
            context.EnsureDownloadAsync,
            context.LoggerFactory.CreateLogger<KokoroSpeechSynthesisProvider>());
    }
}
