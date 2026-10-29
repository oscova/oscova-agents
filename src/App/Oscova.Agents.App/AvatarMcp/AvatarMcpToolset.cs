using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Oscova.Agents.Avatar.Contracts;
using Oscova.Agents.Core.ApiContracts.Avatar;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Infrastructure.Avatar;
using System.ComponentModel;
using System.Text.Json;
using static ModelContextProtocol.Protocol.ElicitRequestParams;

namespace Oscova.Agents.App.AvatarMcp;

/// <summary>Exposes the Avatar HTTP controls without exposing the installation credential.</summary>
[McpServerToolType]
public sealed class AvatarMcpToolset(AvatarRuntimeSettingsService settings, AvatarBridgeService bridge, AvatarSpeechHostClient speechHost, ILogger<AvatarMcpToolset> logger)
{
    /// <summary>Speaks text through the operator's Assistant speech provider.</summary>
    [McpServerTool(Name = "avatar_speak", ReadOnly = false)]
    [Description("Synthesize text with the operator's provider and voice selected in Speech settings, then queue it on the avatar. Requires the matching Assistant host and operator login.")]
    public Task<CallToolResult> SpeakAsync(
        [Description("The text the avatar should say.")] string text,
        CancellationToken cancellationToken) =>
        ExecuteAsync(_ => speechHost.SpeakAsync(text, cancellationToken));

    private const string PairingGuidance = " Saved pairing is restored automatically. Try actions directly. On pairing_required, call avatar_pairing_start, ask the user for the displayed code, then call avatar_pairing_complete. If offline, start the Engine on its machine with oscova avatar start.";

    /// <summary>Reads the configured Engine identity.</summary>
    [McpServerTool(Name = "avatar_identity", ReadOnly = true)]
    [Description("Read the configured Engine identity without pairing." + PairingGuidance)]
    public Task<CallToolResult> ReadIdentityAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(snapshot => bridge.ReadIdentityAsync(snapshot.BridgeUrl, cancellationToken));

    /// <summary>Consumes a user-provided pairing code and privately saves the credential.</summary>
    [McpServerTool(Name = "avatar_pairing_complete")]
    [Description("Pair using the six-digit code the user reads from the Avatar window. The credential is saved privately for future calls.")]
    public Task<CallToolResult> CompletePairingAsync(
        [Description("Six ASCII digits supplied by the user from the Avatar window.")] string pairingCode,
        [Description("Client label from 1 to 100 characters without control characters.")] string clientName,
        CancellationToken cancellationToken) =>
        ExecuteAsync(snapshot => bridge.PairEndpointAsync(snapshot.BridgeUrl, pairingCode, clientName, cancellationToken));

    /// <summary>Activates the requested mode and saves it only for the unchanged target.</summary>
    [McpServerTool(Name = "avatar_activate")]
    [Description("Activate Shared or Exclusive access and save the selected mode after success." + PairingGuidance)]
    public Task<CallToolResult> ActivateAsync(
        [Description("Shared permits shared control. Exclusive reserves the Engine for this paired identity.")] AvatarAccessMode accessMode,
        CancellationToken cancellationToken) => ExecuteAsync(async snapshot =>
    {
        var result = await bridge.SendEndpointAsync(snapshot.BridgeUrl, accessMode, HttpMethod.Post,
            "/avatarbridge/activate", new { accessMode = accessMode.ToString() }, false, cancellationToken).ConfigureAwait(false);
        if (result.IsError) return result;
        try
        {
            if (!await settings.TrySaveAccessModeAsync(snapshot.BridgeUrl, accessMode).ConfigureAwait(false))
                return Error("target_changed", "Activation succeeded for the previous target, but its preference was not saved because the selected Engine changed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogError(ex, "Activated Avatar access mode could not be persisted.");
            return Error("settings_persistence_failed", "Activation succeeded, but the access mode could not be saved.");
        }

        return result;
    });

    /// <summary>Shuts down only the target the user explicitly confirmed.</summary>
    [McpServerTool(Name = "avatar_shutdown", Destructive = true)]
    [Description("Shut down the Engine after explicit client-supported confirmation. MCP remains available." + PairingGuidance)]
    public Task<CallToolResult> ShutdownAsync(McpServer server,
        [Description("True requests immediate shutdown. False allows graceful cleanup.")] bool forceShutdown,
        CancellationToken cancellationToken) => ExecuteAsync(async snapshot =>
    {
        if (server.ClientCapabilities?.Elicitation is null)
            return Error("confirmation_required", "Shutdown requires session-based Boolean elicitation. This client cannot confirm it.");
        var confirmation = await server.ElicitAsync(new ElicitRequestParams
        {
            Message = $"Shut down the Avatar Engine at {snapshot.BridgeUrl}?",
            RequestedSchema = new RequestSchema
            {
                Properties = { ["confirm"] = new BooleanSchema { Title = "Confirm shutdown", Description = "Approve shutting down this Avatar Engine." } },
                Required = ["confirm"]
            }
        }, cancellationToken).ConfigureAwait(false);
        if (confirmation.Action != "accept" || confirmation.Content is null ||
            !confirmation.Content.TryGetValue("confirm", out var confirm) || confirm.ValueKind != JsonValueKind.True)
            return Error("confirmation_required", "Shutdown was not confirmed.");
        var current = await settings.GetSettingsAsync().ConfigureAwait(false);
        if (!string.Equals(current.BridgeUrl.TrimEnd('/'), snapshot.BridgeUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            return Error("target_changed", "The selected Engine changed during confirmation. No shutdown was sent.");
        return await bridge.SendEndpointAsync(snapshot.BridgeUrl, snapshot.PreferredAccessMode, HttpMethod.Post,
            "/avatarbridge/shutdown", new AvatarShutdownRequest { ForceShutdown = forceShutdown }, true, cancellationToken).ConfigureAwait(false);
    });

    /// <summary>Ask the Engine to display a pairing code. Ask the user to read the code, then call avatar_pairing_complete.</summary>
    [McpServerTool(Name = "avatar_pairing_start")]
    [Description("Ask the Engine to display a pairing code. Ask the user to read the code, then call avatar_pairing_complete." + PairingGuidance)]
    public Task<CallToolResult> StartPairingAsync(
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "/avatarbridge/pairing/start", null, false, cancellationToken);

    /// <summary>Queue existing Engine-local audio and its message.</summary>
    [McpServerTool(Name = "avatar_speech_queue_file")]
    [Description("Queue existing Engine-local audio and its message." + PairingGuidance)]
    public Task<CallToolResult> QueueSpeechFileAsync(
        [Description("Audio file on the Engine machine, not the MCP client machine.")] string fileName,
        [Description("Text to display with the request.")] string message,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "/avatarbridge/speech/queue-file", new AvatarSpeechQueueFileRequest { FileName = fileName, Message = message }, true, cancellationToken);

    /// <summary>Download and queue existing audio and its message.</summary>
    [McpServerTool(Name = "avatar_speech_queue")]
    [Description("Download and queue existing audio and its message." + PairingGuidance)]
    public Task<CallToolResult> QueueSpeechAsync(
        [Description("Audio URL reachable from the Engine machine. This does not synthesize speech.")] string url,
        [Description("Text to display with the request.")] string message,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "/avatarbridge/speech/queue", new AvatarSpeechQueueRequest { Url = url, Message = message }, true, cancellationToken);

    /// <summary>Stop speech and lip sync.</summary>
    [McpServerTool(Name = "avatar_speech_stop")]
    [Description("Stop speech and lip sync." + PairingGuidance)]
    public Task<CallToolResult> StopSpeechAsync(
        [Description("True interrupts immediately. False stops gracefully.")] bool forcedStop,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "/avatarbridge/speech/stop", new AvatarSpeechStopRequest { ForcedStop = forcedStop }, true, cancellationToken);

    /// <summary>Set speech volume.</summary>
    [McpServerTool(Name = "avatar_speech_volume")]
    [Description("Set speech volume." + PairingGuidance)]
    public Task<CallToolResult> SetSpeechVolumeAsync(
        [Description("Volume from 0 to 1. Zero also disables lip sync.")] float volume,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "/avatarbridge/speech/volume", new AvatarSpeechVolumeRequest { Volume = volume }, true, cancellationToken);

    /// <summary>Applies both layouts, message duration and layout selection mode.</summary>
    [McpServerTool(Name = "avatar_presentation_settings")]
    [Description("Applies both avatar layouts and their selection mode.\nMode must be Auto, Subtitle or Side.\nDurationSeconds must be finite and at least 0. Zero disables ordinary messages.\nSubtitle and Side are required.\nEach Yaw must be finite from -5 to 5.\nEach FontSize must be an integer from 8 to 120.\nPositionX, PositionY, Red, Green, Blue and Alpha must be finite from 0 to 1.\nWidth and Height must be finite, greater than 0 and at most 1.\nPositionX + Width and PositionY + Height must be at most 1." + PairingGuidance)]
    public Task<CallToolResult> SetPresentationSettingsAsync(
        [Description("Complete configuration with Mode, DurationSeconds, Subtitle and Side. Both presets require Yaw, PositionX, PositionY, Width, Height, FontSize, Red, Green, Blue and Alpha.")] AvatarPresentationSettingsRequest settings,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "/avatarbridge/presentation/settings", settings, true, cancellationToken);

    /// <summary>Display message text without audio.</summary>
    [McpServerTool(Name = "avatar_message_text")]
    [Description("Display message text without audio." + PairingGuidance)]
    public Task<CallToolResult> ShowMessageAsync(
        [Description("Text to display with the request.")] string message,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "/avatarbridge/message/text", new AvatarMessageTextRequest { Message = message }, true, cancellationToken);

    /// <summary>Display a temporary message.</summary>
    [McpServerTool(Name = "avatar_message_display")]
    [Description("Display a temporary message." + PairingGuidance)]
    public Task<CallToolResult> DisplayMessageAsync(
        [Description("Text to display with the request.")] string message,
        [Description("Finite positive display duration in seconds.")] float durationSeconds,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "/avatarbridge/message/display", new AvatarMessageDisplayRequest { Message = message, DurationSeconds = durationSeconds }, true, cancellationToken);

    /// <summary>Clear the message panel.</summary>
    [McpServerTool(Name = "avatar_message_clear")]
    [Description("Clear the message panel." + PairingGuidance)]
    public Task<CallToolResult> ClearMessageAsync(
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "/avatarbridge/message/clear", null, true, cancellationToken);

    /// <summary>List available Avatar names.</summary>
    [McpServerTool(Name = "avatar_list", ReadOnly = true)]
    [Description("List available Avatar names." + PairingGuidance)]
    public Task<CallToolResult> ListAvatarsAsync(
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, "/avatarbridge/avatar/list", null, true, cancellationToken);

    /// <summary>Select an Avatar.</summary>
    [McpServerTool(Name = "avatar_select")]
    [Description("Select an Avatar." + PairingGuidance)]
    public Task<CallToolResult> SelectAvatarAsync(
        [Description("Case-sensitive Avatar name returned by avatar_list.")] string avatarName,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "/avatarbridge/avatar/select", new AvatarSelectRequest { AvatarName = avatarName }, true, cancellationToken);

    /// <summary>List hair options for an Avatar.</summary>
    [McpServerTool(Name = "avatar_hair_list", ReadOnly = true)]
    [Description("List hair options for an Avatar." + PairingGuidance)]
    public Task<CallToolResult> ListHairAsync(
        [Description("Case-sensitive Avatar name returned by avatar_list.")] string avatarName,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, $"/avatarbridge/hair/list?AvatarName={Uri.EscapeDataString(avatarName)}", null, true, cancellationToken);

    /// <summary>Select hair for an Avatar.</summary>
    [McpServerTool(Name = "avatar_hair_select")]
    [Description("Select hair for an Avatar." + PairingGuidance)]
    public Task<CallToolResult> SelectHairAsync(
        [Description("Case-sensitive Avatar name returned by avatar_list.")] string avatarName,
        [Description("Case-sensitive hair ID returned by avatar_hair_list.")] string hairId,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "/avatarbridge/hair/select", new AvatarHairSelectRequest { AvatarName = avatarName, HairId = hairId }, true, cancellationToken);

    /// <summary>List clothing options for an Avatar.</summary>
    [McpServerTool(Name = "avatar_clothing_list", ReadOnly = true)]
    [Description("List clothing options for an Avatar." + PairingGuidance)]
    public Task<CallToolResult> ListClothingAsync(
        [Description("Case-sensitive Avatar name returned by avatar_list.")] string avatarName,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, $"/avatarbridge/clothing/list?AvatarName={Uri.EscapeDataString(avatarName)}", null, true, cancellationToken);

    /// <summary>Select clothing for an Avatar slot.</summary>
    [McpServerTool(Name = "avatar_clothing_select")]
    [Description("Select clothing for an Avatar slot." + PairingGuidance)]
    public Task<CallToolResult> SelectClothingAsync(
        [Description("Case-sensitive Avatar name returned by avatar_list.")] string avatarName,
        [Description("Clothing slot: top or bottom.")] string slot,
        [Description("Case-sensitive clothing ID returned by avatar_clothing_list.")] string clothingId,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "/avatarbridge/clothing/select", new AvatarClothingSelectRequest { AvatarName = avatarName, Slot = slot, ClothingId = clothingId }, true, cancellationToken);

    /// <summary>List available emotions.</summary>
    [McpServerTool(Name = "avatar_list_emotions", ReadOnly = true)]
    [Description("List available emotions." + PairingGuidance)]
    public Task<CallToolResult> ListEmotionsAsync(
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, "/avatarbridge/emotion/list", null, true, cancellationToken);

    /// <summary>Set an emotion with a timed transition.</summary>
    [McpServerTool(Name = "avatar_set_emotion")]
    [Description("Set an emotion with a timed transition." + PairingGuidance)]
    public Task<CallToolResult> SetEmotionAsync(
        [Description("Case-sensitive emotion returned by avatar_list_emotions.")] string emotionName,
        [Description("Positive transition duration in seconds, for example 0.25.")] float blendSpeed,
        CancellationToken cancellationToken,
        [Description("Finite emotion strength from 0 to 1.\nDefault: 1.\nZero removes emotion influence.")] float intensity = 1f) =>
        SendAsync(HttpMethod.Post, "/avatarbridge/emotion/select", new AvatarEmotionRequest { EmotionName = emotionName, BlendSpeed = blendSpeed, Intensity = intensity }, true, cancellationToken);

    /// <summary>Set the background color.</summary>
    [McpServerTool(Name = "avatar_set_background_color")]
    [Description("Set the background color." + PairingGuidance)]
    public Task<CallToolResult> SetBackgroundColorAsync(
        [Description("Red from 0 to 255.")] int red,
        [Description("Green from 0 to 255.")] int green,
        [Description("Blue from 0 to 255.")] int blue,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "/avatarbridge/background/color", new AvatarBackgroundColorRequest { Red = red, Green = green, Blue = blue }, true, cancellationToken);

    /// <summary>Set window resolution and fullscreen.</summary>
    [McpServerTool(Name = "avatar_set_resolution")]
    [Description("Set window resolution and fullscreen." + PairingGuidance)]
    public Task<CallToolResult> SetResolutionAsync(
        [Description("Window width in pixels.")] int width,
        [Description("Window height in pixels.")] int height,
        [Description("Whether to use fullscreen.")] bool fullscreen,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "/avatarbridge/resolution", new AvatarResolutionRequest { Width = width, Height = height, Fullscreen = fullscreen }, true, cancellationToken);

    /// <summary>Set graphics quality and ray tracing.</summary>
    [McpServerTool(Name = "avatar_set_graphics_preset")]
    [Description("Set graphics quality and ray tracing." + PairingGuidance)]
    public Task<CallToolResult> SetGraphicsPresetAsync(
        [Description("0 Low, 1 Medium, 2 High, 3 Epic, 4 Cinematic.")] int level,
        [Description("Whether runtime ray tracing is enabled.")] bool rayTracingEnabled,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "/avatarbridge/graphics/preset", new AvatarGraphicsPresetRequest { Level = level, RayTracingEnabled = rayTracingEnabled }, true, cancellationToken);

    /// <summary>Read authenticated Engine health without activating.</summary>
    [McpServerTool(Name = "avatar_health_check", ReadOnly = true)]
    [Description("Read authenticated Engine health without activating." + PairingGuidance)]
    public Task<CallToolResult> CheckHealthAsync(
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, "/avatarbridge/health", null, false, cancellationToken);

    private Task<CallToolResult> SendAsync(HttpMethod method, string route, object? body, bool requireActivation, CancellationToken cancellationToken) =>
        ExecuteAsync(snapshot => bridge.SendEndpointAsync(snapshot.BridgeUrl, snapshot.PreferredAccessMode,
            method, route, body, requireActivation, cancellationToken));

    // Capture settings once so an action cannot silently switch targets partway through execution.
    private async Task<CallToolResult> ExecuteAsync(Func<AvatarSettings.Snapshot, Task<AvatarBridgeEndpointResult>> action)
    {
        AvatarBridgeEndpointResult result;
        try
        {
            result = await action(await settings.GetSettingsAsync().ConfigureAwait(false)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = Error("cancelled", "The request was cancelled. Its outcome may be unknown. Check the Engine before retrying. No automatic retry was performed.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Avatar MCP operation failed.");
            result = Error("request_failed", "The Avatar request failed. Check the MCP log and shared app_data permissions.");
        }

        return new CallToolResult
        {
            IsError = result.IsError, StructuredContent = result.Payload,
            Content = [new TextContentBlock { Text = result.Payload.GetRawText() }]
        };
    }

    private static AvatarBridgeEndpointResult Error(string code, string description) =>
        AvatarBridgeEndpointResult.Failure(code, description);
}
