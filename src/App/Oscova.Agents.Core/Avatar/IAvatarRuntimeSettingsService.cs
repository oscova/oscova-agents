using Oscova.Agents.Core.Avatar.Contracts;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Avatar.Contracts;

namespace Oscova.Agents.Core.Avatar;

/// <summary>
/// Coordinates persisted avatar runtime settings with the singleton avatar bridge client.
/// </summary>
public interface IAvatarRuntimeSettingsService
{
    /// <summary>Returns a detached snapshot of the shared avatar runtime settings.</summary>
    Task<AvatarSettings.Snapshot> GetSettingsAsync();

    /// <summary>Saves the shared avatar runtime settings without changing the live engine.</summary>
    Task SaveSettingsAsync(AvatarSettings.Snapshot snapshot);

    /// <summary>Saves only the bridge URL and preferred access mode.</summary>
    Task SaveConnectionSettingsAsync(string bridgeUrl, AvatarAccessMode accessMode);

    /// <summary>Connects to the configured bridge and applies saved settings to the live engine.</summary>
    /// <param name="forceApply">True for explicit restoration, false to reuse an unchanged ready snapshot.</param>
    Task<AvatarRuntimeSyncResponse> ApplySavedSettingsAsync(bool forceApply = true);

    /// <summary>
    /// Returns true if the engine is currently connected and configured. If not, attempts a
    /// single connect + apply pass and returns the new state. Subsequent calls short-circuit
    /// until the readiness flag is cleared.
    /// </summary>
    Task<bool> EnsureReadyAsync();

    /// <summary>
    /// Clears the readiness flag so the next <see cref="EnsureReadyAsync"/> call retries
    /// connect + apply. Called when a downstream avatar request fails.
    /// </summary>
    void MarkUnready();
}
