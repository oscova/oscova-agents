namespace Oscova.Agents.Plugins.GoogleWorkspace;

/// <summary>Registers Google account metadata and its Gmail, Calendar and YouTube tools.</summary>
public sealed class GoogleWorkspaceAgentPlugin : IAgentPlugin
{
    /// <summary>Declares account support and per-user tool factories without opening a connection.</summary>
    public void Configure(AgentPluginBuilder builder)
    {
        builder.AddOAuthProvider(GoogleWorkspaceProvider.Definition)
            .AddToolset(context => new GmailToolset(context))
            .AddToolset(context => new GoogleCalendarToolset(context))
            .AddToolset(context => new YouTubeToolset(context));
    }
}
