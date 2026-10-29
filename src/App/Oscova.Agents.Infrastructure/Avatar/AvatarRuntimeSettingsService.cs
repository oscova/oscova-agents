using Microsoft.Extensions.Logging;
using Oscova.Agents.Avatar;
using Oscova.Agents.Avatar.Contracts;
using Oscova.Agents.Core.Avatar;
using Oscova.Agents.Core.Avatar.Contracts;
using Oscova.Agents.Core.Cli;
using Oscova.Agents.Core.Settings;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Oscova.Agents.Infrastructure.Avatar;

/// <summary>
/// Applies saved singleton avatar settings to the stateless external avatar engine on demand
/// and caches the saved snapshot applied by this process so speech can skip redundant apply
/// passes until settings change or a downstream avatar call fails.
/// </summary>
public sealed class AvatarRuntimeSettingsService(
    AvatarSettings avatarSettings,
    IAvatarBridgeService avatarBridgeService,
    ILogger<AvatarRuntimeSettingsService> logger,
    string settingsLockPath)
    : IAvatarRuntimeSettingsService
{
    private readonly SemaphoreSlim _settingsSyncGate = new(1, 1);
    // The external engine is stateless, so readiness is only valid for the
    // saved bridge URL and exact saved snapshot this process successfully applied.
    private ReadyState? _readyState;
    private int _readyInvalidationVersion;

    private sealed record ReadyState(string BridgeUrl, string SettingsFingerprint, long SessionVersion);

    /// <summary>Returns a detached snapshot of the shared avatar runtime settings.</summary>
    public async Task<AvatarSettings.Snapshot> GetSettingsAsync()
    {
        await _settingsSyncGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await ReadSnapshotAsync().ConfigureAwait(false);
        }
        finally
        {
            _settingsSyncGate.Release();
        }
    }

    /// <summary>Saves the shared avatar runtime settings without changing the live engine.</summary>
    public async Task SaveSettingsAsync(AvatarSettings.Snapshot snapshot)
    {
        await _settingsSyncGate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var lease = await AvatarFileLease.AcquireAsync(settingsLockPath, CancellationToken.None).ConfigureAwait(false);
            await avatarSettings.ReloadAsync().ConfigureAwait(false);
            var previousSnapshot = avatarSettings.CreateSnapshot();
            avatarSettings.ApplyFrom(snapshot);
            if (!await avatarSettings.TrySaveAsync().ConfigureAwait(false))
            {
                avatarSettings.ApplyFrom(previousSnapshot);
                throw new InvalidOperationException("Avatar settings could not be saved.");
            }

            _readyState = null;
            unchecked
            {
                _readyInvalidationVersion++;
            }
        }
        finally
        {
            _settingsSyncGate.Release();
        }
    }

    /// <summary>Saves only connection preferences without committing unrelated UI drafts.</summary>
    public Task SaveConnectionSettingsAsync(string bridgeUrl, AvatarAccessMode accessMode) =>
        SaveConnectionAsync(bridgeUrl, accessMode);

    /// <summary>Saves a CLI target without overwriting the current access mode.</summary>
    public Task SaveBridgeUrlAsync(string bridgeUrl) => SaveConnectionAsync(bridgeUrl, null);

    private async Task SaveConnectionAsync(string bridgeUrl, AvatarAccessMode? accessMode)
    {
        await _settingsSyncGate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var lease = await AvatarFileLease.AcquireAsync(settingsLockPath, CancellationToken.None).ConfigureAwait(false);
            await avatarSettings.ReloadAsync().ConfigureAwait(false);
            var previousSnapshot = avatarSettings.CreateSnapshot();
            avatarSettings.BridgeUrl = bridgeUrl;
            if (accessMode.HasValue) avatarSettings.PreferredAccessMode = accessMode.Value;
            if (!await avatarSettings.TrySaveAsync().ConfigureAwait(false))
            {
                avatarSettings.ApplyFrom(previousSnapshot);
                throw new InvalidOperationException("Avatar connection settings could not be saved.");
            }

            _readyState = null;
            unchecked
            {
                _readyInvalidationVersion++;
            }
        }
        finally
        {
            _settingsSyncGate.Release();
        }
    }

    /// <summary>Connects to the configured bridge and applies saved settings to the live engine.</summary>
    public async Task<AvatarRuntimeSyncResponse> ApplySavedSettingsAsync(bool forceApply = true)
    {
        await _settingsSyncGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var snapshot = await ReadSnapshotAsync().ConfigureAwait(false);
            var bridgeUrl = NormalizeBridgeUrl(snapshot.BridgeUrl);
            var fingerprint = ComputeSettingsFingerprint(snapshot);
            var invalidationVersion = Volatile.Read(ref _readyInvalidationVersion);

            if (string.IsNullOrWhiteSpace(bridgeUrl))
            {
                return new AvatarRuntimeSyncResponse
                {
                    Success = false,
                    Description = "Avatar bridge URL is not configured."
                };
            }

            var connection = await avatarBridgeService
                .ConnectAsync(bridgeUrl, snapshot.PreferredAccessMode)
                .ConfigureAwait(false);
            if (!connection.Success)
            {
                _readyState = null;
                return new AvatarRuntimeSyncResponse
                {
                    Success = false,
                    Connected = false,
                    ErrorCode = connection.ErrorCode,
                    RetryAfterSeconds = connection.RetryAfterSeconds,
                    Description = connection.Description
                };
            }

            var sessionVersion = avatarBridgeService.SessionVersion;
            // Explicit apply restores live-only changes even when the saved snapshot is unchanged.
            if (!forceApply && _readyState is { } readyState &&
                string.Equals(readyState.BridgeUrl, bridgeUrl, StringComparison.Ordinal) &&
                string.Equals(readyState.SettingsFingerprint, fingerprint, StringComparison.Ordinal) &&
                readyState.SessionVersion == sessionVersion)
            {
                return new AvatarRuntimeSyncResponse
                {
                    Success = true,
                    Connected = true,
                    SettingsApplied = true,
                    Description = "Saved avatar settings were already applied."
                };
            }

            var applyFailure = await ApplySnapshotAsync(snapshot).ConfigureAwait(false);
            if (applyFailure is not null)
            {
                _readyState = null;
                return new AvatarRuntimeSyncResponse
                {
                    Success = false,
                    Connected = true,
                    SettingsApplied = false,
                    Description = applyFailure
                };
            }

            if (Volatile.Read(ref _readyInvalidationVersion) != invalidationVersion)
            {
                _readyState = null;
                return new AvatarRuntimeSyncResponse
                {
                    Success = false,
                    Connected = true,
                    SettingsApplied = false,
                    Description = "Avatar readiness was invalidated while settings were being applied."
                };
            }

            if (avatarBridgeService.SessionVersion != sessionVersion)
            {
                _readyState = null;
                return new AvatarRuntimeSyncResponse
                {
                    Success = false,
                    Connected = true,
                    SettingsApplied = false,
                    Description = "Avatar Engine session changed while settings were being applied."
                };
            }

            _readyState = new ReadyState(bridgeUrl, fingerprint, sessionVersion);

            return new AvatarRuntimeSyncResponse
            {
                Success = true,
                Connected = true,
                SettingsApplied = true,
                Description = "Saved avatar settings were applied."
            };
        }
        finally
        {
            _settingsSyncGate.Release();
        }
    }

    /// <summary>Saves an activated access mode only while the selected Engine still matches.</summary>
    public async Task<bool> TrySaveAccessModeAsync(string expectedBridgeUrl, AvatarAccessMode accessMode)
    {
        await _settingsSyncGate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var lease = await AvatarFileLease.AcquireAsync(settingsLockPath, CancellationToken.None).ConfigureAwait(false);
            await avatarSettings.ReloadAsync().ConfigureAwait(false);
            if (!string.Equals(NormalizeBridgeUrl(avatarSettings.BridgeUrl), NormalizeBridgeUrl(expectedBridgeUrl), StringComparison.OrdinalIgnoreCase))
                return false;

            var previousSnapshot = avatarSettings.CreateSnapshot();
            avatarSettings.PreferredAccessMode = accessMode;
            if (!await avatarSettings.TrySaveAsync().ConfigureAwait(false))
            {
                avatarSettings.ApplyFrom(previousSnapshot);
                throw new InvalidOperationException("Avatar access mode could not be saved.");
            }

            MarkUnready();
            return true;
        }
        finally
        {
            _settingsSyncGate.Release();
        }
    }

    // Release the file lease before any connection or rendering work starts.
    private async Task<AvatarSettings.Snapshot> ReadSnapshotAsync()
    {
        using var lease = await AvatarFileLease.AcquireAsync(settingsLockPath, CancellationToken.None).ConfigureAwait(false);
        await avatarSettings.ReloadAsync().ConfigureAwait(false);
        return avatarSettings.CreateSnapshot();
    }

    public async Task<bool> EnsureReadyAsync()
    {
        var result = await ApplySavedSettingsAsync(forceApply: false).ConfigureAwait(false);
        return result.Success;
    }

    public void MarkUnready()
    {
        unchecked
        {
            Interlocked.Increment(ref _readyInvalidationVersion);
        }

        _readyState = null;
    }

    private async Task<string?> ApplySnapshotAsync(AvatarSettings.Snapshot snapshot)
    {
        // Push durable visual and audio configuration into the stateless engine.
        var failure = ValidateResponse(
            "resolution",
            await avatarBridgeService.ChangeResolutionAsync(snapshot.ResolutionWidth, snapshot.ResolutionHeight, snapshot.IsFullscreen));
        if (failure is not null) { return failure; }

        failure = ValidateResponse(
            "graphics preset",
            await avatarBridgeService.SetGraphicsPresetAsync(snapshot.GraphicsPresetLevel, snapshot.IsRayTracingEnabled));
        if (failure is not null) { return failure; }

        failure = ValidateResponse(
            "background color",
            await avatarBridgeService.ChangeBackgroundColorAsync(snapshot.BackgroundColorRed, snapshot.BackgroundColorGreen, snapshot.BackgroundColorBlue));
        if (failure is not null) { return failure; }

        failure = ValidateResponse(
            "speech volume",
            await avatarBridgeService.SetSpeechVolumeAsync(snapshot.SpeechVolume));
        if (failure is not null) { return failure; }

        if (!string.IsNullOrWhiteSpace(snapshot.SelectedAvatar))
        {
            failure = ValidateResponse(
                "avatar selection",
                await avatarBridgeService.SelectAvatarAsync(snapshot.SelectedAvatar));
            if (failure is not null) { return failure; }

            // A rejected cosmetic option must not prevent avatar speech from becoming ready.
            if (!string.IsNullOrWhiteSpace(snapshot.SelectedHairId))
            {
                try
                {
                    var hairFailure = ValidateResponse("hair selection", await avatarBridgeService.SelectHairAsync(
                        snapshot.SelectedAvatar, snapshot.SelectedHairId).ConfigureAwait(false));
                    if (hairFailure is not null)
                    {
                        logger.LogWarning("{Failure} Avatar: {AvatarName}, hair ID: {HairId}.",
                            hairFailure, snapshot.SelectedAvatar, snapshot.SelectedHairId);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not apply saved hair {HairId} to avatar {AvatarName}.",
                        snapshot.SelectedHairId, snapshot.SelectedAvatar);
                }
            }

            if (!string.IsNullOrWhiteSpace(snapshot.SelectedTopClothingId))
            {
                try
                {
                    var clothingFailure = ValidateResponse("top clothing selection", await avatarBridgeService.SelectClothingAsync(
                        snapshot.SelectedAvatar, "top", snapshot.SelectedTopClothingId).ConfigureAwait(false));
                    if (clothingFailure is not null)
                    {
                        logger.LogWarning("{Failure} Avatar: {AvatarName}, clothing ID: {ClothingId}.",
                            clothingFailure, snapshot.SelectedAvatar, snapshot.SelectedTopClothingId);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not apply saved top clothing {ClothingId} to avatar {AvatarName}.",
                        snapshot.SelectedTopClothingId, snapshot.SelectedAvatar);
                }
            }
        }

        // Avatar selection prepares the camera before the chosen presentation takes ownership.
        failure = ValidateResponse("presentation settings", await avatarBridgeService.SetPresentationSettingsAsync(snapshot.Presentation));
        if (failure is not null) { return failure; }

        if (!string.IsNullOrWhiteSpace(snapshot.SelectedEmotion))
        {
            failure = ValidateResponse(
                "emotion selection",
                await avatarBridgeService.SetEmotionAsync(snapshot.SelectedEmotion, snapshot.EmotionBlendSpeed));
            if (failure is not null) { return failure; }
        }

        logger.LogInformation("Applied saved avatar runtime settings to the avatar engine at {BridgeUrl}.", NormalizeBridgeUrl(snapshot.BridgeUrl));
        return null;
    }

    private static string NormalizeBridgeUrl(string? bridgeUrl)
    {
        return string.IsNullOrWhiteSpace(bridgeUrl)
            ? string.Empty
            : bridgeUrl.Trim().TrimEnd('/');
    }

    private static string ComputeSettingsFingerprint(AvatarSettings.Snapshot snapshot)
    {
        var builder = new StringBuilder();
        Append(builder, "avatar", snapshot.SelectedAvatar);
        Append(builder, "hair", snapshot.SelectedHairId);
        Append(builder, "topClothing", snapshot.SelectedTopClothingId);
        Append(builder, "accessMode", snapshot.PreferredAccessMode.ToString());
        Append(builder, "emotion", snapshot.SelectedEmotion);
        Append(builder, "blend", snapshot.EmotionBlendSpeed);
        Append(builder, "background", $"{snapshot.BackgroundColorRed},{snapshot.BackgroundColorGreen},{snapshot.BackgroundColorBlue}");
        Append(builder, "resolution", $"{snapshot.ResolutionWidth}x{snapshot.ResolutionHeight}");
        Append(builder, "fullscreen", snapshot.IsFullscreen);
        Append(builder, "graphics", snapshot.GraphicsPresetLevel);
        Append(builder, "rayTracing", snapshot.IsRayTracingEnabled);
        Append(builder, "presentation", System.Text.Json.JsonSerializer.Serialize(snapshot.Presentation));
        Append(builder, "speechVolume", snapshot.SpeechVolume);

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(bytes);
    }

    private static void Append(StringBuilder builder, string key, string? value)
    {
        builder
            .Append(key)
            .Append('=')
            .Append(value?.Trim() ?? string.Empty)
            .Append('\n');
    }

    private static void Append(StringBuilder builder, string key, int value) =>
        Append(builder, key, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder builder, string key, bool value) =>
        Append(builder, key, value ? "true" : "false");

    private static void Append(StringBuilder builder, string key, float value) =>
        Append(builder, key, FormatFloat(value));

    private static string FormatFloat(float value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    private static string? ValidateResponse(string command, IAvatarBridgeResponse? response)
    {
        if (response is null)
        {
            return $"Avatar engine did not return a response for {command}.";
        }

        if (response.Success)
        {
            return null;
        }

        var description = string.IsNullOrWhiteSpace(response.Description)
            ? "The avatar engine rejected the command."
            : response.Description;
        return $"Avatar engine failed to apply {command}: {description}";
    }
}
