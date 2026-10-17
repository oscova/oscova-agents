using Oscova.Agents.Channels;
using Oscova.Agents.Credentials;

namespace Oscova.Agents.Plugins.Slack;

/// <summary>Declares Slack's identity fields, credentials, and connection factory.</summary>
public sealed class SlackChannel : IAgentChannel
{
    private readonly HashSet<string> _activeApps = new(StringComparer.Ordinal);
    /// <summary>Identifies the permitted workspace.</summary>
    public const string WorkspaceId = "workspace_id";
    /// <summary>Identifies the permitted member.</summary>
    public const string AuthorizedUserId = "authorized_user_id";
    /// <summary>Identifies the bot's OAuth token.</summary>
    public const string BotToken = "botToken";
    /// <summary>Identifies the Socket Mode app token.</summary>
    public const string AppLevelToken = "appLevelToken";

    /// <summary>Gets the bot and app credential declaration.</summary>
    public static CredentialDeclaration Credential { get; } = new()
    {
        Key = "slack-bot",
        Label = "Slack Bot",
        LabelKey = PluginTextKeys.CredentialLabel,
        Description = "Bot User OAuth Token and App-Level Token used for a private Slack connection.",
        DescriptionKey = PluginTextKeys.CredentialDescription,
        Scope = CredentialScope.User,
        Fields =
        [
            new() { Name = BotToken, Label = "Bot User OAuth Token", LabelKey = PluginTextKeys.CredentialBotTokenLabel, IsSecret = true, IsRequired = true },
            new() { Name = AppLevelToken, Label = "App-Level Token", LabelKey = PluginTextKeys.CredentialAppLevelTokenLabel, IsSecret = true, IsRequired = true }
        ]
    };

    /// <summary>Gets the channel configuration declaration.</summary>
    public ChannelDefinition Definition { get; } = new()
    {
        Id = "slack",
        Title = "Slack",
        TitleKey = "Settings.Section.Slack.Label",
        Description = "Workspace members can see the installed Slack app. Oscova responds only to direct messages from the authorized member.",
        DescriptionKey = "Settings.Slack.ConfigurationDescription",
        IconResourceName = "Oscova.Agents.Plugins.Slack.Icons.channel.svg",
        Help = new PluginHelpTopic("settings", "Oscova.Agents.Plugins.Slack.Help.settings.md"),
        SupportsNotifications = false,
        CredentialKeys = [Credential.Key]
    };

    /// <inheritdoc />
    public async Task<ChannelRuntimeConfiguration> ReadConfigurationAsync(AgentPluginContext context, bool requireComplete, CancellationToken ct = default)
    {
        var settings = await SlackSettings.LoadAsync(context, ct).ConfigureAwait(false);
        var errors = settings.Validate(requireComplete || settings.StartAtStartup);
        if (errors.Count > 0) { throw new ArgumentException(string.Join(" ", errors.Values.Select(error => error.Message))); }
        return new(settings.StartAtStartup, settings.AgentId,
            connection => new SlackChannelConnection(context, connection, settings, _activeApps));
    }
}
