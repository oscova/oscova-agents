using Microsoft.Extensions.Logging;

namespace Oscova.Agents.Plugins.FoundryLocal;

/// <summary>Registers local inference through Microsoft Foundry Local.</summary>
public sealed class FoundryLocalAgentPlugin : IAgentPlugin
{
    public void Configure(AgentPluginBuilder builder)
    {
        builder.AddModelProvider(context => new FoundryLocalModelProvider(
            context.LoggerFactory.CreateLogger<FoundryLocalModelProvider>(),
            Path.Combine(context.ModelStorageDirectory, "foundry-local")));
    }
}
