using LLama.Native;
using LLama.Abstractions;
using Microsoft.Extensions.Logging;
using System.Reflection;

namespace Oscova.Agents.Plugins.LlamaSharp;

/// <summary>Registers local GGUF inference through the model-provider catalog.</summary>
public sealed class LlamaSharpAgentPlugin : IAgentPlugin
{
    /// <summary>Adds the LLamaSharp model provider to the plugin builder.</summary>
    public void Configure(AgentPluginBuilder builder)
    {
        builder.UseResources<PluginText>().AddModelProvider(context =>
        {
            // Edition selection is fixed for the process and never silently falls back to CPU.
            NativeLibraryConfig.All.WithSearchDirectory(context.PluginDirectory)
                .WithCuda(IsCudaEdition).WithVulkan(false).WithAutoFallback(false);

            if (IsCudaEdition)
            {
                NativeLibraryConfig.All.WithSelectingPolicy(new CudaLibraryPolicy(context.PluginDirectory));
            }

            return new LlamaSharpModelProvider(
                context.LoggerFactory.CreateLogger<LlamaSharpModelProvider>(),
                Path.Combine(context.ModelStorageDirectory, "gguf"), IsCudaEdition);
        }).AddSettingsPanel(new("model", PluginSettingsSlots.ModelProvider, Llm.ModelProviderKeys.Local,
            typeof(LlamaSharpSettingsComponent), context => context, PluginSettingsAccess.CurrentUser));
    }

    // The upstream default policy can include CPU even when automatic fallback is disabled.
    private sealed class CudaLibraryPolicy(string pluginDirectory) : INativeLibrarySelectingPolicy
    {
        public IEnumerable<INativeLibrary> Apply(NativeLibraryConfig.Description description, SystemInfo systemInfo,
            NativeLogConfig.LLamaLogCallback? logCallback = null)
        {
            yield return new CudaLibrary(pluginDirectory,
                new NativeLibraryWithCuda(13, description.Library, description.AvxLevel, description.SkipCheck));
        }
    }

    private sealed class CudaLibrary(string pluginDirectory, INativeLibrary library) : INativeLibrary
    {
        public NativeLibraryMetadata? Metadata => library.Metadata;

        public IEnumerable<string> Prepare(SystemInfo systemInfo, NativeLogConfig.LLamaLogCallback? logCallback = null)
        {
            var version = typeof(LlamaSharpAgentPlugin).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(attribute => attribute.Key == "LlamaSharpRuntimeVersion").Value!;
            var package = OperatingSystem.IsWindows() ? "llamasharp.backend.cuda13.windows" : "llamasharp.backend.cuda13.linux";

            foreach (var path in library.Prepare(systemInfo, logCallback))
            {
                yield return Path.Combine(pluginDirectory, "packages", package, version, "LLamaSharpRuntimes",
                    path["runtimes/".Length..]);
            }
        }
    }

    internal static bool IsCudaEdition =>
#if LLAMASHARP_CUDA
        true;
#else
        false;
#endif
}
