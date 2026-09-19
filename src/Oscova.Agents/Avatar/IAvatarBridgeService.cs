using Oscova.Agents.Avatar.Contracts;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Avatar
{
    /// <summary>
    /// Defines operations supported by an Oscova Avatar Engine bridge.
    /// </summary>
    public interface IAvatarBridgeService
    {
        /// <summary>Gets the base URL currently targeted by the bridge.</summary>
        string? ConnectedBaseUrl { get; }

        /// <summary>Gets the local version of the currently authenticated engine session.</summary>
        long SessionVersion { get; }

        /// <summary>
        /// Connects to the avatar engine at the specified base URL.
        /// </summary>
        /// <param name="baseUrl">The base URL of the avatar engine API.</param>
        /// <param name="accessMode">The requested runtime access mode.</param>
        /// <returns>The connection and activation result.</returns>
        Task<AvatarBridgeOperationResult> ConnectAsync(
            string baseUrl,
            AvatarAccessMode accessMode = AvatarAccessMode.Shared);

        /// <summary>Asks the engine to display a short-lived pairing code.</summary>
        /// <param name="baseUrl">The base URL of the avatar engine API.</param>
        /// <returns>The pairing-start result and code lifetime.</returns>
        Task<AvatarPairingStartResult> StartPairingAsync(string baseUrl);

        /// <summary>Completes pairing with the code displayed by the engine.</summary>
        /// <param name="baseUrl">The base URL of the avatar engine API.</param>
        /// <param name="pairingCode">The six-digit code displayed by the engine.</param>
        /// <returns>A nonsecret pairing result.</returns>
        Task<AvatarBridgeOperationResult> CompletePairingAsync(string baseUrl, string pairingCode);

        /// <summary>
        /// Queues a local speech file for lip-sync and speech playback.
        /// </summary>
        /// <param name="fileName">The audio file name to play (e.g., "speech_001.wav").</param>
        /// <param name="message">The text message corresponding to the speech.</param>
        /// <returns>Response indicating success and description of the operation.</returns>
        Task<AvatarSpeechQueueFileResponse> QueueFileSpeechAsync(string fileName, string message);

        /// <summary>
        /// Queues speech playback using a remote audio URL.
        /// The avatar bridge downloads the URL to a local file before enqueueing playback.
        /// </summary>
        /// <param name="url">The remote audio URL to download and play.</param>
        /// <param name="message">The text message corresponding to the speech.</param>
        /// <returns>Response indicating success, description, and saved local file path.</returns>
        Task<AvatarSpeechQueueResponse> QueueSpeechAsync(string url, string message);

        /// <summary>
        /// Stops ongoing speech playback and lip-sync animation.
        /// </summary>
        /// <param name="forcedStop">True to interrupt immediately. False to stop gracefully (recommended).</param>
        /// <returns>Response indicating whether the stop request was processed.</returns>
        Task<AvatarSpeechStopResponse> StopSpeechAsync(bool forcedStop = false);

        /// <summary>
        /// Sets the speech playback volume.
        /// Values outside the valid range (0.0 to 1.0) are rejected by the avatar engine.
        /// </summary>
        /// <param name="volume">Volume level between 0.0 (muted) and 1.0 (full volume).
        /// Note: Setting volume to 0.0 will disable lip sync. For lip sync to work, the volume must be greater than zero (recommended: 0.01).</param>
        /// <returns>Response indicating success, description, and the applied volume level.</returns>
        Task<AvatarSpeechVolumeResponse> SetSpeechVolumeAsync(float volume);

        /// <summary>
        /// Retrieves the avatar names reported by the running engine.
        /// </summary>
        /// <returns>Response containing success status, description, and array of avatar names.</returns>
        Task<AvatarListResponse> ListAvatarsAsync();

        /// <summary>
        /// Selects the current avatar by name.
        /// Avatar name must match one of the available avatars (case-sensitive).
        /// Switching avatars may cause a brief loading period.
        /// </summary>
        /// <param name="avatarName">A case-sensitive name returned by <see cref="ListAvatarsAsync"/>.</param>
        /// <returns>Response indicating success, description, and the selected avatar name.</returns>
        Task<AvatarSelectResponse> SelectAvatarAsync(string avatarName);

        /// <summary>
        /// Retrieves the hair options configured for an avatar.
        /// </summary>
        /// <param name="avatarName">A case-sensitive name returned by <see cref="ListAvatarsAsync"/>.</param>
        /// <returns>Response containing the avatar name and its available hair options.</returns>
        Task<AvatarHairListResponse> ListHairOptionsAsync(string avatarName);

        /// <summary>
        /// Selects a configured hair option for an avatar.
        /// </summary>
        /// <param name="avatarName">A case-sensitive name returned by <see cref="ListAvatarsAsync"/>.</param>
        /// <param name="hairId">A case-sensitive identifier returned by <see cref="ListHairOptionsAsync"/>.</param>
        /// <returns>Response indicating whether the hair selection was accepted.</returns>
        Task<AvatarHairSelectResponse> SelectHairAsync(string avatarName, string hairId);

        /// <summary>
        /// Retrieves the clothing options configured for an avatar.
        /// </summary>
        /// <param name="avatarName">A case-sensitive name returned by <see cref="ListAvatarsAsync"/>.</param>
        /// <returns>Response containing the avatar name and its available clothing options.</returns>
        Task<AvatarClothingListResponse> ListClothingOptionsAsync(string avatarName);

        /// <summary>
        /// Selects a configured clothing option for one avatar slot.
        /// </summary>
        /// <param name="avatarName">A case-sensitive name returned by <see cref="ListAvatarsAsync"/>.</param>
        /// <param name="slot">The case-sensitive slot, currently <c>top</c> or <c>bottom</c>.</param>
        /// <param name="clothingId">A case-sensitive identifier returned by <see cref="ListClothingOptionsAsync"/>.</param>
        /// <returns>Response indicating whether the clothing selection was accepted.</returns>
        Task<AvatarClothingSelectResponse> SelectClothingAsync(string avatarName, string slot, string clothingId);

        /// <summary>
        /// Retrieves the emotion names reported by the running engine.
        /// </summary>
        /// <returns>Response containing success status, description, and array of emotion names.</returns>
        Task<AvatarEmotionListResponse> ListEmotionsAsync();

        /// <summary>
        /// Sets a target emotion with smooth transition.
        /// The emotion will transition over the specified blend speed duration.
        /// Emotion name must match one of the available emotions (case-sensitive).
        /// </summary>
        /// <param name="emotionName">A case-sensitive name returned by <see cref="ListEmotionsAsync"/>.</param>
        /// <param name="blendSpeed">Duration in seconds for the emotion transition. Must be greater than zero. A value of 0.25 is recommended.</param>
        /// <returns>Response indicating success, description, selected emotion, and blend speed applied.</returns>
        Task<AvatarEmotionResponse> SetEmotionAsync(string emotionName, float blendSpeed);

        /// <summary>
        /// Changes the background color using RGB components.
        /// Values outside the valid range (0-255) are rejected by the avatar engine.
        /// </summary>
        /// <param name="red">Red component (0-255).</param>
        /// <param name="green">Green component (0-255).</param>
        /// <param name="blue">Blue component (0-255).</param>
        /// <returns>Response indicating success, description, and the applied RGB values.</returns>
        Task<AvatarBackgroundColorResponse> ChangeBackgroundColorAsync(int red, int green, int blue);

        /// <summary>
        /// Sets the avatar message font size.
        /// </summary>
        /// <param name="fontSize">Message font size from 8 through 120.</param>
        /// <returns>Response indicating success, description, and the applied font size.</returns>
        Task<AvatarMessageFontSizeResponse> SetMessageFontSizeAsync(int fontSize);

        /// <summary>
        /// Sets the avatar message color and opacity using normalized RGBA values.
        /// </summary>
        /// <param name="red">Normalized red component from 0.0 through 1.0.</param>
        /// <param name="green">Normalized green component from 0.0 through 1.0.</param>
        /// <param name="blue">Normalized blue component from 0.0 through 1.0.</param>
        /// <param name="alpha">Normalized alpha component from 0.0 through 1.0.</param>
        /// <returns>Response indicating success, description, and the applied normalized RGBA values.</returns>
        Task<AvatarMessageColorAndOpacityResponse> SetMessageColorAndOpacityAsync(float red, float green, float blue, float alpha);

        /// <summary>
        /// Shows text on the avatar message panel without playing audio.
        /// </summary>
        /// <param name="message">The non-empty text to display.</param>
        /// <returns>Response indicating success, description, and the displayed text.</returns>
        Task<AvatarMessageTextResponse> ShowMessageAsync(string message);

        /// <summary>
        /// Clears the avatar message panel immediately.
        /// </summary>
        /// <returns>Response indicating success, description, and whether the panel was cleared.</returns>
        Task<AvatarMessageClearResponse> ClearMessageAsync();

        /// <summary>
        /// Sets the avatar message box position while preserving its current size.
        /// </summary>
        /// <param name="positionX">Horizontal message box position as a screen fraction from 0.0 through 1.0.</param>
        /// <param name="positionY">Vertical message box position as a screen fraction from 0.0 through 1.0.</param>
        /// <returns>Response indicating success, description, and the applied message box position.</returns>
        Task<AvatarMessageBoxPositionResponse> SetMessageBoxPositionAsync(float positionX, float positionY);

        /// <summary>
        /// Sets the avatar message box size while preserving its current position.
        /// </summary>
        /// <param name="width">Message box width as a screen fraction greater than 0.0 through 1.0.</param>
        /// <param name="height">Message box height as a screen fraction greater than 0.0 through 1.0.</param>
        /// <returns>Response indicating success, description, and the applied message box size.</returns>
        Task<AvatarMessageBoxSizeResponse> SetMessageBoxSizeAsync(float width, float height);

        /// <summary>
        /// Sets how long avatar messages remain visible.
        /// </summary>
        /// <param name="durationSeconds">Message display duration in seconds. The avatar bridge accepts 0.0 or greater.</param>
        /// <returns>Response indicating success, description, and the applied display duration.</returns>
        Task<AvatarMessageDisplayDurationResponse> SetMessageDisplayDurationAsync(float durationSeconds);

        /// <summary>
        /// Changes the engine/window resolution and fullscreen mode.
        /// </summary>
        /// <param name="width">Window width in pixels. The avatar engine accepts 1 through 7680.</param>
        /// <param name="height">Window height in pixels. The avatar engine accepts 1 through 4320.</param>
        /// <param name="fullscreen">Whether to enable fullscreen mode.</param>
        /// <returns>Response indicating success, description, and the applied resolution settings.</returns>
        Task<AvatarResolutionResponse> ChangeResolutionAsync(int width, int height, bool fullscreen);

        /// <summary>
        /// Sets the global graphics quality preset.
        /// Level values map to known presets (0=Low, 1=Medium, 2=High, 3=Epic, 4=Cinematic).
        /// </summary>
        /// <param name="level">The graphics preset level from 0 through 4.</param>
        /// <param name="isRayTracingEnabled">Whether runtime ray tracing should be enabled.</param>
        /// <returns>Response indicating success, description, applied level, resolved preset name, and effective ray-tracing state.</returns>
        Task<AvatarGraphicsPresetResponse> SetGraphicsPresetAsync(int level, bool isRayTracingEnabled);

        /// <summary>
        /// Performs a lightweight health/status check for the running avatar engine.
        /// </summary>
        /// <returns>Response containing success status, engine status, version, and port.</returns>
        Task<AvatarHealthCheckResponse> CheckHealthAsync();

        /// <summary>
        /// Adjusts camera yaw (rotation around vertical axis).
        /// The avatar engine expects a value from -5.0 through 5.0.
        /// Negative values rotate the camera left, positive values rotate right.
        /// </summary>
        /// <param name="yaw">Engine yaw value from -5.0 through 5.0.</param>
        /// <returns>Response indicating success, description, and the applied yaw value.</returns>
        Task<AvatarCameraYawResponse> SetCameraYawAsync(float yaw);

        /// <summary>
        /// Requests a shutdown of the avatar engine.
        /// The response is sent before the engine terminates.
        /// </summary>
        /// <param name="forceShutdown">If true, immediately terminates the engine. If false, allows graceful cleanup before shutdown.</param>
        /// <param name="ct">Cancellation token for the active run.</param>
        /// <returns>Response indicating success, description, and whether shutdown was initiated.</returns>
        Task<AvatarShutdownResponse> ShutdownAsync(bool forceShutdown = false, CancellationToken ct = default);
    }
}
