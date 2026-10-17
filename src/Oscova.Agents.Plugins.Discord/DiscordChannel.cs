using Oscova.Agents.Channels;
using Oscova.Agents.Credentials;

namespace Oscova.Agents.Plugins.Discord;

/// <summary>Declares Discord's identity, settings, and connection factory.</summary>
public sealed class DiscordChannel : IAgentChannel
{
    /// <summary>Identifies the configured Discord sender.</summary>
    public const string AuthorizedUserId = "authorized_user_id";
    /// <summary>Identifies the bot token within the declared credential.</summary>
    public const string BotToken = "botToken";

    /// <summary>Gets the bot credential declaration.</summary>
    public static CredentialDeclaration Credential { get; } = new()
    {
        Key = "discord-bot",
        Label = "Discord Bot",
        LabelKey = PluginTextKeys.CredentialLabel,
        Description = "Bot token used to connect the Discord bot.",
        DescriptionKey = PluginTextKeys.CredentialDescription,
        Fields = [new() { Name = BotToken, Label = "Bot Token", LabelKey = PluginTextKeys.CredentialBotTokenLabel }]
    };

    /// <summary>Gets the channel configuration declaration.</summary>
    public ChannelDefinition Definition { get; } = new()
    {
        Id = "discord",
        Title = "Discord",
        TitleKey = "Settings.Section.Discord.Label",
        Description = "Set up Discord bot integration and channel bindings.",
        DescriptionKey = "Settings.Section.Discord.Description",
        IconResourceName = "Oscova.Agents.Plugins.Discord.Icons.channel.svg",
        Help = new PluginHelpTopic("settings", "Oscova.Agents.Plugins.Discord.Help.settings.md"),
        SupportsNotifications = true,
        CredentialKeys = [Credential.Key]
    };

    /// <inheritdoc />
    public async Task<ChannelRuntimeConfiguration> ReadConfigurationAsync(AgentPluginContext context, bool requireComplete, CancellationToken ct = default)
    {
        var settings = await DiscordSettings.LoadAsync(context, ct).ConfigureAwait(false);
        var errors = settings.Validate(requireComplete || settings.StartAtStartup);
        if (errors.Count > 0) { throw new ArgumentException(string.Join(" ", errors.Values.Select(error => error.Message))); }
        return new(settings.StartAtStartup, settings.AgentId,
            connection => new DiscordChannelConnection(context, connection, settings));
    }
}
