using Oscova.Agents.Speech;
#if LOCAL_SPEECH_CUDA
using System.Reflection;
#endif
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using Whisper.net.LibraryLoader;

namespace Oscova.Agents.Plugins.LocalSpeechRecognition;

/// <summary>Registers the local speech recognition provider.</summary>
public sealed class LocalSpeechRecognitionAgentPlugin : IAgentPlugin
{
#if LOCAL_SPEECH_CUDA
    internal const string Backend = "CUDA";
#else
    internal const string Backend = "CPU";
#endif

    /// <summary>Adds the local Whisper provider.</summary>
    public void Configure(AgentPluginBuilder builder)
    {
        builder.UseResources<PluginText>()
            .AddSpeechRecognitionProvider(CreateRecognitionProvider)
            .AddSettingsPanel(new("recognition", PluginSettingsSlots.SpeechRecognition, SpeechProviderKeys.Local,
                typeof(SpeechRecognitionSettingsComponent), context => context, PluginSettingsAccess.CurrentUser));
    }

    private static WhisperSpeechRecognitionProvider CreateRecognitionProvider(AgentPluginContext context)
    {
        var rid = GetSupportedRid();
#if LOCAL_SPEECH_CUDA
        var packageId = OperatingSystem.IsWindows()
            ? "oscova.agents.native.whisper.cuda.windows"
            : "oscova.agents.native.whisper.cuda.linux";
        var nativeRoot = Path.Combine(context.PluginDirectory, "packages", packageId,
            GetDependencyVersion("WhisperNativePackageVersion"), "runtimes", rid, "native");
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

#if LOCAL_SPEECH_CUDA
    // The SDK emits these values from the same properties used by package references.
    private static string GetDependencyVersion(string key) =>
        typeof(LocalSpeechRecognitionAgentPlugin).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == key).Value!;
#endif

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

        if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            return "osx-x64";
        }

        throw new PlatformNotSupportedException(
            "Local Speech supports win-x64, linux-x64, osx-x64, and osx-arm64.");
    }

}
