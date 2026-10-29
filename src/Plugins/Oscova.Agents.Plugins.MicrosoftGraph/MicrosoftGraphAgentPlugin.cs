namespace Oscova.Agents.Plugins.MicrosoftGraph;

/// <summary>Registers Microsoft account metadata and Outlook and To Do tools.</summary>
public sealed class MicrosoftGraphAgentPlugin : IAgentPlugin
{
    /// <summary>Declares account support and per-user tool factories.</summary>
    public void Configure(AgentPluginBuilder builder)
    {
        builder.AddOAuthProvider(MicrosoftGraphProvider.Definition)
            .AddToolset(context => new OutlookToolset(context))
            .AddToolset(context => new MicrosoftTodoToolset(context));
    }
}
