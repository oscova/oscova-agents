using Oscova.Agents.Channels;

namespace Oscova.Agents.Plugins.Slack;

/// <summary>Registers Slack configuration and shares app reservations between its connections.</summary>
public sealed class SlackAgentPlugin : IAgentPlugin
{
    /// <summary>Declares Slack without connecting to the service.</summary>
    public void Configure(AgentPluginBuilder builder)
    {
        builder.UseResources<PluginText>()
            .AddCredential(SlackChannel.Credential)
            .AddChannel(new SlackChannel())
            .AddSettingsPanel(new("settings", PluginSettingsSlots.Page, null,
                typeof(SlackSettingsComponent), context => context, PluginSettingsAccess.CurrentUser,
                Help: new PluginHelpTopic("settings", "Oscova.Agents.Plugins.Slack.Help.settings.md"))
            {
                Page = new("channels", "Slack", new SlackChannel().Definition.Description,
                    new SlackChannel().Definition.TitleKey, new SlackChannel().Definition.DescriptionKey,
                    "Oscova.Agents.Plugins.Slack.Icons.channel.svg")
            });
    }
}
