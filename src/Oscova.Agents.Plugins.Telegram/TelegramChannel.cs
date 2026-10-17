using Oscova.Agents.Channels;
using Oscova.Agents.Credentials;

namespace Oscova.Agents.Plugins.Telegram;

/// <summary>Declares Telegram's identity, settings, and connection factory.</summary>
public sealed class TelegramChannel : IAgentChannel
{
    /// <summary>Identifies the configured Telegram sender.</summary>
    public const string AuthorizedUserId = "authorized_user_id";
    /// <summary>Identifies the bot token within the declared credential.</summary>
    public const string BotToken = "botToken";

    /// <summary>Gets the bot credential declaration.</summary>
    public static CredentialDeclaration Credential { get; } = new()
    {
        Key = "telegram-bot",
        Label = "Telegram Bot",
        LabelKey = PluginTextKeys.CredentialLabel,
        Description = "Bot token used to connect the Telegram bot.",
        DescriptionKey = PluginTextKeys.CredentialDescription,
        Fields = [new() { Name = BotToken, Label = "Bot Token", LabelKey = PluginTextKeys.CredentialBotTokenLabel }]
    };

    /// <summary>Gets the channel configuration declaration.</summary>
    public ChannelDefinition Definition { get; } = new()
    {
        Id = "telegram",
        Title = "Telegram",
        TitleKey = "Settings.Section.Telegram.Label",
        Description = "Set up Telegram bot integration and private chat access.",
        DescriptionKey = "Settings.Section.Telegram.Description",
        IconResourceName = "Oscova.Agents.Plugins.Telegram.Icons.channel.svg",
        Help = new PluginHelpTopic("settings", "Oscova.Agents.Plugins.Telegram.Help.settings.md"),
        SupportsNotifications = false,
        CredentialKeys = [Credential.Key]
    };

    /// <inheritdoc />
    public async Task<ChannelRuntimeConfiguration> ReadConfigurationAsync(AgentPluginContext context, bool requireComplete, CancellationToken ct = default)
    {
        var settings = await TelegramSettings.LoadAsync(context, ct).ConfigureAwait(false);
        var errors = settings.Validate(requireComplete || settings.StartAtStartup);
        if (errors.Count > 0) { throw new ArgumentException(string.Join(" ", errors.Values.Select(error => error.Message))); }
        return new(settings.StartAtStartup, settings.AgentId,
            connection => new TelegramChannelConnection(context, connection, settings));
    }
}
