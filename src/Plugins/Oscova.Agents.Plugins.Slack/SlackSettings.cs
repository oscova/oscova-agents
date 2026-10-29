using Oscova.Agents.Channels;
using Oscova.Agents.Persistence;
using Oscova.Agents.Plugins.Slack.Settings;

namespace Oscova.Agents.Plugins.Slack;

/// <summary>Owns Slack's per-user configuration, validation and encrypted YAML persistence.</summary>
public sealed record SlackSettings : OfficialChannelConfiguration
{
    /// <summary>Gets the authorized sender's identifier.</summary>
    public string AuthorizedUserId { get; init; } = string.Empty;
    /// <summary>Gets the workspace admitted by this connection.</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>Reads this plugin's model from its chosen current-user bound store.</summary>
    public static async Task<SlackSettings> LoadAsync(AgentPluginContext context, CancellationToken ct = default)
    {
        var content = await Store(context).ReadAsync("channel", ct).ConfigureAwait(false);
        return content is null ? new() : new YamlConverter().Deserialize<SlackSettings>(content)
            ?? throw new InvalidDataException("Slack configuration must be a YAML mapping.");
    }

    /// <summary>Atomically saves this plugin's YAML through the encrypted current-user store.</summary>
    public Task SaveAsync(AgentPluginContext context, CancellationToken ct = default) =>
        Store(context).WriteAsync("channel", new YamlConverter().Serialize(this), ct);

    private static IPluginSettings Store(AgentPluginContext context) => context.UserSettings
        ?? throw new InvalidOperationException("Slack settings require an authenticated current-user store.");

    /// <summary>Validates vendor fields with the existing readiness rules.</summary>
    public IReadOnlyDictionary<string, ChannelValidationError> Validate(bool complete)
    {
        var errors = new Dictionary<string, ChannelValidationError>();
        if (complete)
        {
            if (string.IsNullOrWhiteSpace(WorkspaceId)) { errors[SlackChannel.WorkspaceId] = new("Slack workspace ID is required.", PluginTextKeys.ValidationWorkspaceId); }
            if (string.IsNullOrWhiteSpace(AuthorizedUserId)) { errors[SlackChannel.AuthorizedUserId] = new("Authorized Slack member ID is required.", PluginTextKeys.ValidationAuthorizedUserId); }
        }
        return errors;
    }
}
