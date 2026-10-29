using System.Globalization;
using Oscova.Agents.Channels;
using Oscova.Agents.Persistence;
using Oscova.Agents.Plugins.Telegram.Settings;

namespace Oscova.Agents.Plugins.Telegram;

/// <summary>Owns Telegram's per-user configuration, validation and encrypted YAML persistence.</summary>
public sealed record TelegramSettings : OfficialChannelConfiguration
{
    /// <summary>Gets the authorized sender's identifier.</summary>
    public string AuthorizedUserId { get; init; } = string.Empty;

    /// <summary>Reads this plugin's model from its chosen current-user bound store.</summary>
    public static async Task<TelegramSettings> LoadAsync(AgentPluginContext context, CancellationToken ct = default)
    {
        var content = await Store(context).ReadAsync("channel", ct).ConfigureAwait(false);
        return content is null ? new() : new YamlConverter().Deserialize<TelegramSettings>(content)
            ?? throw new InvalidDataException("Telegram configuration must be a YAML mapping.");
    }

    /// <summary>Atomically saves this plugin's YAML through the encrypted current-user store.</summary>
    public Task SaveAsync(AgentPluginContext context, CancellationToken ct = default) =>
        Store(context).WriteAsync("channel", new YamlConverter().Serialize(this), ct);

    private static IPluginSettings Store(AgentPluginContext context) => context.UserSettings
        ?? throw new InvalidOperationException("Telegram settings require an authenticated current-user store.");

    /// <summary>Validates vendor fields with the existing readiness rules.</summary>
    public IReadOnlyDictionary<string, ChannelValidationError> Validate(bool complete)
    {
        var errors = new Dictionary<string, ChannelValidationError>();
        var value = AuthorizedUserId;
        if (!string.IsNullOrWhiteSpace(value) || complete)
        {
            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            { errors[TelegramChannel.AuthorizedUserId] = new("Authorized User ID must be an integer from 1 to 9223372036854775807.", PluginTextKeys.ValidationAuthorizedUserId); }
        }
        return errors;
    }
}
