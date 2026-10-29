using Oscova.Agents.Core;
using Oscova.Agents.Infrastructure.Utils;
using Oscova.Agents.Secrets;
using Oscova.Agents.Settings;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Infrastructure.Settings;

/// <summary>Keeps host settings readable at startup and encrypts user settings.</summary>
public sealed class AppSettingsRepository(IContentProtector protector) : IAppSettingsRepository
{
    private readonly FileSettingsRepository _host = new(_ => PathUtil.SettingsPath);

    /// <inheritdoc />
    public Task<string?> GetSettingsAsync(string scopeId, string settingsKey, CancellationToken ct = default) =>
        string.Equals(scopeId, WellKnownUsers.SystemId, StringComparison.Ordinal)
            ? _host.GetSettingsAsync(scopeId, settingsKey, ct)
            : EncryptedFileContent.ReadAllTextAsync(scopeId, ResolveUserPath(scopeId, settingsKey), protector, ct);

    /// <inheritdoc />
    public Task SaveSettingsAsync(string scopeId, string settingsKey, string content, CancellationToken ct = default) =>
        string.Equals(scopeId, WellKnownUsers.SystemId, StringComparison.Ordinal)
            ? _host.SaveSettingsAsync(scopeId, settingsKey, content, ct)
            : EncryptedFileContent.WriteAllTextAsync(scopeId, ResolveUserPath(scopeId, settingsKey), content, protector, ct);

    private static string ResolveUserPath(string userId, string settingsKey)
    {
        if (!Guid.TryParse(userId, out _) || !PathHelper.IsSafePathSegment(settingsKey))
        {
            throw new ArgumentException("A valid user ID and settings key are required.");
        }

        var root = Path.Combine(PathUtil.UsersPath, userId, UserFileNames.SettingsDirectory);
        if (!PathHelper.TryResolveContainedPath(root, settingsKey + ".yaml.enc", out var path))
        {
            throw new UnauthorizedAccessException("The settings path escapes the user's directory.");
        }

        return path;
    }
}