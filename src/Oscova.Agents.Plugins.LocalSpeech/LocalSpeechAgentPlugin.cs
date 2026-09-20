using KokoroSharp;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using Whisper.net.LibraryLoader;

namespace Oscova.Agents.Plugins.LocalSpeech;

/// <summary>Registers Oscova's local speech recognition and synthesis providers.</summary>
public sealed class LocalSpeechAgentPlugin : IAgentPlugin
{
#if LOCAL_SPEECH_CUDA
    private const string WhisperNativePackageVersion = "2.0.0";
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
        var rid = GetSupportedRid();
#if LOCAL_SPEECH_CUDA
        var packageId = OperatingSystem.IsWindows()
            ? "oscova.agents.native.whisper.cuda.windows"
            : "oscova.agents.native.whisper.cuda.linux";
        var nativeRoot = Path.Combine(context.PluginDirectory, "packages", packageId,
            WhisperNativePackageVersion, "runtimes", rid, "native");
        // Whisper resolves its CUDA subtree relative to the parent of LibraryPath.
        RuntimeOptions.LibraryPath = nativeRoot + Path.DirectorySeparatorChar;
        RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cuda, RuntimeLibrary.Cpu];
#else
        var nativeRoot = Path.Combine(context.PluginDirectory, "runtimes", rid, "native");
        // Whisper resolves its CPU runtime tree relative to the parent of LibraryPath.
        RuntimeOptions.LibraryPath = nativeRoot + Path.DirectorySeparatorChar;
        RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu];
#endif
        return new WhisperSpeechRecognitionProvider(
            Path.Combine(context.ModelStorageDirectory, "ggml-base.bin"),
            context.EnsureDownloadAsync,
            context.LoggerFactory.CreateLogger<WhisperSpeechRecognitionProvider>());
    }

    private static string GetSupportedRid()
    {
        if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            return "win-x64";
        }

        if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            return "linux-x64";
        }

        if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            return "osx-arm64";
        }

        throw new PlatformNotSupportedException(
            "Local Speech supports win-x64, linux-x64, and osx-arm64.");
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
