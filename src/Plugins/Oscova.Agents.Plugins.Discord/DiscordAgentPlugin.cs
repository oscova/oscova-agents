using Oscova.Agents.Channels;

namespace Oscova.Agents.Plugins.Discord;

/// <summary>Registers Discord configuration and its optional connection.</summary>
public sealed class DiscordAgentPlugin : IAgentPlugin
{
    /// <summary>Declares Discord without connecting to the service.</summary>
    public void Configure(AgentPluginBuilder builder)
    {
        builder.UseResources<PluginText>()
            .AddCredential(DiscordChannel.Credential)
            .AddChannel(new DiscordChannel())
            .AddSettingsPanel(new("settings", PluginSettingsSlots.Page, null,
                typeof(DiscordSettingsComponent), context => context, PluginSettingsAccess.CurrentUser,
                Help: new PluginHelpTopic("settings", "Oscova.Agents.Plugins.Discord.Help.settings.md"))
            {
                Page = new("channels", "Discord", new DiscordChannel().Definition.Description,
                    new DiscordChannel().Definition.TitleKey, new DiscordChannel().Definition.DescriptionKey,
                    "Oscova.Agents.Plugins.Discord.Icons.channel.svg")
            });
    }
}
