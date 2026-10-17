using Oscova.Agents.Channels;

namespace Oscova.Agents.Plugins.Telegram;

/// <summary>Registers Telegram configuration and its optional connection.</summary>
public sealed class TelegramAgentPlugin : IAgentPlugin
{
    /// <summary>Declares Telegram without connecting to the service.</summary>
    public void Configure(AgentPluginBuilder builder)
    {
        builder.UseResources<PluginText>()
            .AddCredential(TelegramChannel.Credential)
            .AddChannel(new TelegramChannel())
            .AddSettingsPanel(new("settings", PluginSettingsSlots.Page, null,
                typeof(TelegramSettingsComponent), context => context, PluginSettingsAccess.CurrentUser,
                Help: new PluginHelpTopic("settings", "Oscova.Agents.Plugins.Telegram.Help.settings.md"))
            {
                Page = new("channels", "Telegram", new TelegramChannel().Definition.Description,
                    new TelegramChannel().Definition.TitleKey, new TelegramChannel().Definition.DescriptionKey,
                    "Oscova.Agents.Plugins.Telegram.Icons.channel.svg")
            });
    }
}
