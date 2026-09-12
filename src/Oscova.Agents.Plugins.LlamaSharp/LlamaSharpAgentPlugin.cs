using LLama.Native;
using Microsoft.Extensions.Logging;

namespace Oscova.Agents.Plugins.LlamaSharp;

/// <summary>Registers local GGUF inference through the model-provider catalog.</summary>
public sealed class LlamaSharpAgentPlugin : IAgentPlugin
{
    /// <summary>Adds the LLamaSharp model provider to the plugin builder.</summary>
    public void Configure(AgentPluginBuilder builder)
    {
        builder.AddModelProvider(context =>
        {
            // LLamaSharp selects the CPU variant from the installed plugin runtime tree.
            NativeLibraryConfig.All.WithSearchDirectory(context.PluginDirectory);
            return new LlamaSharpModelProvider(
                context.LoggerFactory.CreateLogger<LlamaSharpModelProvider>(),
                Path.Combine(context.ModelStorageDirectory, "gguf"));
        });
    }
}
