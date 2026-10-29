using Oscova.Agents.Infrastructure.Avatar;
using ModelContextProtocol.Protocol;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Avatar;
using Oscova.Agents.Avatar.Contracts;
using Oscova.Agents.Core.Avatar;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Harness;
using System.ComponentModel;
using System.Text.Json;
using static ModelContextProtocol.Protocol.ElicitRequestParams;

namespace Oscova.Agents.Infrastructure.Toolsets;

/// <summary>
/// Provides agents with operational controls for the shared avatar runtime.
/// </summary>
public class AvatarToolset : IAgentToolset
{
    private readonly IAvatarBridgeService _avatarBridgeService;
    private readonly IElicitationRouter _elicitationRouter;
    private readonly RunContextAccessor _runContextAccessor;
    private readonly IAvatarRuntimeSettingsService _avatarRuntimeSettingsService;
    private readonly AvatarSpeechService _avatarSpeech;
    private readonly ILogger<AvatarToolset> _logger;
    private readonly IUserPermissionService _permissions;

    public AvatarToolset(
        IAvatarBridgeService avatarBridgeService,
        IElicitationRouter elicitationRouter,
        RunContextAccessor runContextAccessor,
        IAvatarRuntimeSettingsService avatarRuntimeSettingsService,
        AvatarSpeechService avatarSpeech,
        ILogger<AvatarToolset> logger,
        IUserPermissionService permissions)
    {
        _avatarBridgeService = avatarBridgeService;
        _elicitationRouter = elicitationRouter;
        _runContextAccessor = runContextAccessor;
        _avatarRuntimeSettingsService = avatarRuntimeSettingsService;
        _avatarSpeech = avatarSpeech;
        _logger = logger;
        _permissions = permissions;
    }

    [DisplayName("connect")]
    [AgentTool]
    [Description("Connects to the avatar engine and verifies connectivity.\nRequires an authenticated operator run.")]
    public async Task<string> ConnectAsync(
        [Description("Base URL of the avatar engine (default: http://127.0.0.1:11318)")] string baseUrl = AvatarSettings.DefaultBridgeUrl)
    {
        await RequireOperatorAsync().ConfigureAwait(false);
        try
        {
            var settings = await _avatarRuntimeSettingsService.GetSettingsAsync().ConfigureAwait(false);
            var configuredUrl = settings.BridgeUrl.Trim().TrimEnd('/');
            var requestedUrl = baseUrl.Trim().TrimEnd('/');
            if (string.Equals(configuredUrl, requestedUrl, StringComparison.OrdinalIgnoreCase))
            {
                var sync = await _avatarRuntimeSettingsService.ApplySavedSettingsAsync().ConfigureAwait(false);
                if (!sync.Success)
                {
                    throw new ToolFailureException(sync.Description, "Open Avatar Settings to pair or resolve access.");
                }
            }
            else
            {
                var connection = await _avatarBridgeService
                    .ConnectAsync(baseUrl, settings.PreferredAccessMode)
                    .ConfigureAwait(false);
                if (!connection.Success)
                {
                    throw new ToolFailureException(connection.Description, "Open Avatar Settings to pair or resolve access.");
                }
            }

            var health = await _avatarBridgeService.CheckHealthAsync().ConfigureAwait(false);
            return $"Successfully connected to avatar engine at {baseUrl}. Status: {health.Status}, Version: {health.Version}";
        }
        catch (Exception ex) when (ex is not ToolFailureException and not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to connect to the avatar engine.");
            throw;
        }
    }

    [DisplayName("list")]
    [AgentTool]
    [Description("Lists available avatars in the engine.\nRequires an authenticated operator run.")]
    public async Task<string> ListAvatarsAsync()
    {
        await RequireOperatorAsync().ConfigureAwait(false);
        try
        {
            var result = await _avatarBridgeService.ListAvatarsAsync().ConfigureAwait(false);

            if (!result.Success)
            {
                throw BridgeFailure("list avatars", result.Description);
            }

            return $"Available avatars: {string.Join(", ", result.Avatars)}";
        }
        catch (InvalidOperationException)
        {
            throw NotConnected();
        }
        catch (Exception ex) when (ex is not ToolFailureException and not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to list avatars unexpectedly.");
            throw;
        }
    }

    [DisplayName("select")]
    [AgentTool]
    [Description("Selects an avatar by name (case-sensitive).\nRequires an authenticated operator run.")]
    public async Task<string> SelectAvatarAsync(
        [Description("Avatar name reported by the connected engine")] string avatarName)
    {
        await RequireOperatorAsync().ConfigureAwait(false);
        try
        {
            var result = await _avatarBridgeService.SelectAvatarAsync(avatarName).ConfigureAwait(false);

            if (!result.Success)
            {
                throw BridgeFailure("select an avatar", result.Description);
            }

            return $"Selected avatar: {result.SelectedAvatar}";
        }
        catch (InvalidOperationException)
        {
            throw NotConnected();
        }
        catch (Exception ex) when (ex is not ToolFailureException and not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to select an avatar unexpectedly.");
            throw;
        }
    }

    [DisplayName("list_emotions")]
    [AgentTool]
    [Description("Lists available emotions for the avatar.\nRequires an authenticated operator run.")]
    public async Task<string> ListEmotionsAsync()
    {
        await RequireOperatorAsync().ConfigureAwait(false);
        try
        {
            var result = await _avatarBridgeService.ListEmotionsAsync().ConfigureAwait(false);

            if (!result.Success)
            {
                throw BridgeFailure("list avatar emotions", result.Description);
            }

            return $"Available emotions: {string.Join(", ", result.Emotions)}";
        }
        catch (InvalidOperationException)
        {
            throw NotConnected();
        }
        catch (Exception ex) when (ex is not ToolFailureException and not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to list avatar emotions unexpectedly.");
            throw;
        }
    }

    [DisplayName("set_emotion")]
    [AgentTool]
    [Description("Sets the avatar's emotion with smooth transition.\nRequires an authenticated operator run.")]
    public async Task<string> SetEmotionAsync(
        [Description("Emotion name reported by the connected engine")] string emotionName,
        [Description("Transition duration in seconds, greater than 0. Default: 0.25.")] float blendSpeed = 0.25f,
        [Description("Finite emotion strength from 0 to 1.\nDefault: 1.\nZero removes emotion influence.")] float intensity = 1f)
    {
        await RequireOperatorAsync().ConfigureAwait(false);
        try
        {
            var result = await _avatarBridgeService.SetEmotionAsync(emotionName, blendSpeed, intensity).ConfigureAwait(false);

            if (!result.Success)
            {
                throw BridgeFailure("set the avatar emotion", result.Description);
            }

            return $"Set emotion to '{result.SelectedEmotion}' with blend speed {result.BlendSpeed}s and intensity {result.Intensity}";
        }
        catch (InvalidOperationException)
        {
            throw NotConnected();
        }
        catch (Exception ex) when (ex is not ToolFailureException and not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to set the avatar emotion unexpectedly.");
            throw;
        }
    }

    [DisplayName("set_background_color")]
    [AgentTool]
    [Description("Changes the avatar background color.\nRequires an authenticated operator run.")]
    public async Task<string> SetBackgroundColorAsync(
        [Description("Red component (0-255)")] int red,
        [Description("Green component (0-255)")] int green,
        [Description("Blue component (0-255)")] int blue)
    {
        await RequireOperatorAsync().ConfigureAwait(false);
        try
        {
            var result = await _avatarBridgeService.ChangeBackgroundColorAsync(red, green, blue).ConfigureAwait(false);

            if (!result.Success)
            {
                throw BridgeFailure("set the avatar background color", result.Description);
            }

            return $"Set background color to RGB({result.Red}, {result.Green}, {result.Blue})";
        }
        catch (InvalidOperationException)
        {
            throw NotConnected();
        }
        catch (Exception ex) when (ex is not ToolFailureException and not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to set the avatar background color unexpectedly.");
            throw;
        }
    }

    [DisplayName("set_resolution")]
    [AgentTool]
    [Description("Changes the avatar engine resolution.\nRequires an authenticated operator run.")]
    public async Task<string> SetResolutionAsync(
        [Description("Width in pixels")] int width,
        [Description("Height in pixels")] int height,
        [Description("Enable fullscreen mode")] bool fullscreen = false)
    {
        await RequireOperatorAsync().ConfigureAwait(false);
        try
        {
            var result = await _avatarBridgeService.ChangeResolutionAsync(width, height, fullscreen).ConfigureAwait(false);

            if (!result.Success)
            {
                throw BridgeFailure("set the avatar resolution", result.Description);
            }

            var fullscreenText = result.Fullscreen ? " (fullscreen)" : "";
            return $"Set resolution to {result.Width}x{result.Height}{fullscreenText}";
        }
        catch (InvalidOperationException)
        {
            throw NotConnected();
        }
        catch (Exception ex) when (ex is not ToolFailureException and not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to set the avatar resolution unexpectedly.");
            throw;
        }
    }

    /// <summary>Applies both user-calibrated layouts and their mode to the running avatar.</summary>
    [DisplayName("set_presentation_settings")]
    [AgentTool]
    [Description("Applies both avatar layouts and their selection mode using a complete settings configuration.\nRequires an authenticated operator run.")]
    public async Task<string> SetPresentationSettingsAsync(
        [Description("Complete configuration with Mode, DurationSeconds, Subtitle and Side.\nMode must be Auto, Subtitle or Side.\nDurationSeconds must be finite and at least 0. Zero disables ordinary messages.\nSubtitle and Side are required.\nBoth presets require Yaw, PositionX, PositionY, Width, Height, FontSize, Red, Green, Blue and Alpha.\nEach Yaw must be finite from -5 to 5.\nEach FontSize must be an integer from 8 to 120.\nPositionX, PositionY, Red, Green, Blue and Alpha must be finite from 0 to 1.\nWidth and Height must be finite, greater than 0 and at most 1.\nPositionX + Width and PositionY + Height must be at most 1.")] AvatarPresentationSettingsRequest settings)
    {
        await RequireOperatorAsync().ConfigureAwait(false);
        try
        {
            var result = await _avatarBridgeService.SetPresentationSettingsAsync(settings).ConfigureAwait(false);
            if (!result.Success) { throw BridgeFailure("apply avatar presentation settings", result.Description); }

            return $"Avatar presentation settings accepted. Mode: {result.Settings.Mode}.";
        }
        catch (InvalidOperationException)
        {
            throw NotConnected();
        }
        catch (Exception ex) when (ex is not ToolFailureException and not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to apply avatar presentation settings unexpectedly.");
            throw;
        }
    }

    [DisplayName("show_message")]
    [AgentTool]
    [Description("Shows text on the avatar message panel without playing audio.\nRequires an authenticated operator run.")]
    public async Task<string> ShowMessageAsync(
        [Description("The text to show on the avatar message panel")] string message)
    {
        await RequireOperatorAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ToolFailureException("Avatar message text is required.");
        }

        try
        {
            var result = await _avatarBridgeService.ShowMessageAsync(message.Trim()).ConfigureAwait(false);

            if (!result.Success)
            {
                throw BridgeFailure("show the avatar message", result.Description);
            }

            return "Avatar message shown.";
        }
        catch (InvalidOperationException)
        {
            throw NotConnected();
        }
        catch (Exception ex) when (ex is not ToolFailureException and not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to show the avatar message unexpectedly.");
            throw;
        }
    }

    [DisplayName("clear_message")]
    [AgentTool]
    [Description("Clears the avatar message panel.\nRequires an authenticated operator run.")]
    public async Task<string> ClearMessageAsync()
    {
        await RequireOperatorAsync().ConfigureAwait(false);
        try
        {
            var result = await _avatarBridgeService.ClearMessageAsync().ConfigureAwait(false);

            if (!result.Success)
            {
                throw BridgeFailure("clear the avatar message", result.Description);
            }

            if (!result.Cleared)
            {
                throw BridgeFailure("clear the avatar message", "The bridge did not confirm the request.");
            }

            return "Avatar message cleared.";
        }
        catch (InvalidOperationException)
        {
            throw NotConnected();
        }
        catch (Exception ex) when (ex is not ToolFailureException and not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to clear the avatar message unexpectedly.");
            throw;
        }
    }

    [DisplayName("check_health")]
    [AgentTool]
    [Description("Checks the avatar engine status.\nRequires an authenticated operator run.")]
    public async Task<string> HealthCheckAsync()
    {
        await RequireOperatorAsync().ConfigureAwait(false);
        try
        {
            var result = await _avatarBridgeService.CheckHealthAsync().ConfigureAwait(false);

            if (!result.Success)
            {
                throw BridgeFailure("check avatar engine health", result.Status);
            }

            return $"Avatar engine is running. Status: {result.Status}, Version: {result.Version}, Port: {result.Port}";
        }
        catch (InvalidOperationException)
        {
            throw NotConnected();
        }
        catch (Exception ex) when (ex is not ToolFailureException and not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to check avatar engine health unexpectedly.");
            throw;
        }
    }

    /// <summary>
    /// Synthesizes speech and hands the generated WAV file to the shared avatar runtime.
    /// </summary>
    [DisplayName("speak")]
    [AgentTool]
    [Description("Speaks text through the shared avatar using the operator's provider and voice selected in Speech settings.\nRequires an authenticated operator run.")]
    public async Task<string> SpeakAsync(
        [Description("The text the avatar should say")] string text,
        CancellationToken cancellationToken = default)
    {
        // The run supplies trusted identity to the shared operator-only speech operation.
        var userId = _runContextAccessor.Current?.UserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ToolFailureException("Avatar speech requires an authenticated operator run.");
        }

        var result = await _avatarSpeech.SpeakAsync(userId, text, cancellationToken).ConfigureAwait(false);
        var description = result.Payload.TryGetProperty("description", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
        if (result.IsError)
        {
            throw new ToolFailureException(description ?? "Avatar speech failed. Check the Assistant log.");
        }

        return "Avatar speech request accepted by the Engine.";
    }

    [DisplayName("shutdown")]
    [AgentTool]
    [Description("Shuts down the avatar engine.\nRequires an authenticated operator run.")]
    public async Task<string> ShutdownAsync(
        [Description("Force immediate shutdown without cleanup")] bool forceShutdown = false,
        CancellationToken token = default)
    {
        await RequireOperatorAsync().ConfigureAwait(false);
        try
        {
            var context = _runContextAccessor.Current?.Elicitation;
            if (context is null)
            {
                throw new ToolFailureException("Avatar shutdown requires an active user context.");
            }

            // Ask the user if they want to shutdown the avatar engine
            var confirmedTarget = _avatarBridgeService.ConnectedBaseUrl;
            var shutdownSchema = new RequestSchema
            {
                Properties =
                {
                    ["Answer"] = new BooleanSchema()
                }
            };

            var shutdownResponse = await _elicitationRouter.RouteElicitationAsync(
                new ElicitRequestParams
                {
                    Message = "Please confirm you want to shutdown the avatar engine.",
                    RequestedSchema = shutdownSchema,
                },
                context,
                token).ConfigureAwait(false);

            // Check if user wants to shutdown
            if (shutdownResponse.Action != "accept")
            {
                return "Operation cancelled: User did not confirm avatar engine shutdown.";
            }

            // Safely read the "Answer" value from the response content
            if (shutdownResponse.Content is null ||
                !shutdownResponse.Content.TryGetValue("Answer", out var answerElement))
            {
                throw new ToolFailureException("The avatar shutdown confirmation response was invalid.");
            }

            if (answerElement.ValueKind == JsonValueKind.False)
            {
                return "Operation declined: User chose not to shutdown the avatar engine.";
            }

            if (answerElement.ValueKind != JsonValueKind.True)
            {
                throw new ToolFailureException("The avatar shutdown confirmation response was invalid.");
            }

            // Approval belongs to the connection shown before elicitation.
            if (string.IsNullOrWhiteSpace(confirmedTarget) || !string.Equals(confirmedTarget, _avatarBridgeService.ConnectedBaseUrl, StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolFailureException("The connected Avatar Engine changed. Confirm shutdown for the current Engine.");
            }

            var result = await _avatarBridgeService.ShutdownAsync(confirmedTarget, forceShutdown, token).ConfigureAwait(false);

            if (!result.Success)
            {
                throw BridgeFailure("shut down the avatar engine", result.Description);
            }

            if (!result.ShutdownInitiated)
            {
                throw BridgeFailure("shut down the avatar engine", "The bridge did not confirm the shutdown.");
            }

            return "Avatar engine shutdown initiated successfully.";
        }
        catch (InvalidOperationException)
        {
            throw NotConnected();
        }
        catch (Exception ex) when (ex is not ToolFailureException and not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to shut down the avatar engine unexpectedly.");
            throw;
        }
    }

    private async Task RequireOperatorAsync()
    {
        // Agent approval cannot grant control over the operator's shared Avatar runtime.
        var userId = _runContextAccessor.Current?.UserId;
        if (string.IsNullOrWhiteSpace(userId) || !await _permissions.IsOperatorAsync(userId).ConfigureAwait(false))
        {
            throw new ToolFailureException("Avatar control requires an authenticated operator run.");
        }
    }
    private ToolFailureException BridgeFailure(string operation, string? detail)
    {
        _logger.LogError("Avatar bridge could not {Operation}. Detail: {Detail}", operation, detail);
        return new ToolFailureException("The avatar request failed.", "Reconnect the avatar engine and try again.");
    }

    private static ToolFailureException NotConnected() =>
        new("The avatar bridge is not connected.", "Use avatar__connect first.");

}
