using KokoroSharp;
using Microsoft.Extensions.Logging;
using Whisper.net.LibraryLoader;

namespace Oscova.Agents.Plugins.LocalSpeech;

/// <summary>Registers Oscova's local speech recognition and synthesis providers.</summary>
public sealed class LocalSpeechAgentPlugin : IAgentPlugin
{
#if LOCAL_SPEECH_CUDA
    private const string WhisperPayloadVersion = "1.9.1";
    internal const string Backend = "CUDA";
#else
    internal const string Backend = "CPU";
#endif

    /// <summary>Adds the local Whisper and Kokoro providers.</summary>
    public void Configure(AgentPluginBuilder builder)
    {
        builder
            .AddSpeechRecognitionProvider(CreateRecognitionProvider)
            .AddSpeechSynthesisProvider(CreateSynthesisProvider);
    }

    private static WhisperSpeechRecognitionProvider CreateRecognitionProvider(AgentPluginContext context)
    {
#if LOCAL_SPEECH_CUDA
        var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        var packageId = OperatingSystem.IsWindows()
            ? "oscova.agents.native.whisper.cuda.windows"
            : "oscova.agents.native.whisper.cuda.linux";
        var nativeRoot = Path.Combine(context.PluginDirectory, "packages", packageId,
            WhisperPayloadVersion, "runtimes", rid, "native");
        // Whisper resolves its CUDA subtree relative to the parent of LibraryPath.
        RuntimeOptions.LibraryPath = nativeRoot + Path.DirectorySeparatorChar;
        RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cuda, RuntimeLibrary.Cpu];
#else
        // Whisper's probe cannot see the plugin's standard RID tree, so let the plugin load context resolve it.
        RuntimeOptions.LoadedLibrary = RuntimeLibrary.Cpu;
#endif
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
