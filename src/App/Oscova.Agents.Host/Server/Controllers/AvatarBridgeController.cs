using System.Security.Claims;
using System.Text.Json;
using Oscova.Agents.Core.ApiContracts.Avatar;
using Oscova.Agents.Infrastructure.Avatar;
using Oscova.Agents.Infrastructure.Utils;
using Oscova.Agents.Utils;
using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oscova.Agents.Avatar;
using Oscova.Agents.Avatar.Contracts;
using Oscova.Agents.Core.Avatar;
using Oscova.Agents.Core.Avatar.Contracts;
using Oscova.Agents.Core.ApiContracts.AvatarBridge;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Host.Hosting;
using Oscova.Agents.Host.Services;

namespace Oscova.Agents.Host.Server.Controllers;

[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/[controller]")]
public class AvatarBridgeController(
    IAvatarBridgeService avatarBridgeService,
    IAvatarRuntimeSettingsService avatarRuntimeSettingsService,
    AvatarSpeechService speech,
    ILogger<AvatarBridgeController> logger,
    AvatarEngineHostOptions avatarOptions) : ControllerBase
{
    private const string LocalEngineExecutableName = "OscovaAvatarEngine.exe";
    private readonly IAvatarBridgeService _avatarBridgeService = avatarBridgeService;
    private readonly IAvatarRuntimeSettingsService _avatarRuntimeSettingsService = avatarRuntimeSettingsService;
    private readonly ILogger<AvatarBridgeController> _logger = logger;

    private static string LocalEngineDirectory => Path.Combine(
        Path.GetDirectoryName(RuntimeDescriptorWriter.RuntimePath)!,
        "avatar",
        "engine");

    /// <summary>Returns whether the managed local Avatar Engine executable is installed.</summary>
    [HttpGet("local-engine/installed")]
    public ActionResult<bool> IsLocalEngineInstalled() =>
        Ok(OperatingSystem.IsWindows() && HasLocalEngineExecutable(LocalEngineDirectory));

    /// <summary>Starts the local Avatar Engine for LAN access on the requested port.</summary>
    [HttpPost("local-engine/start")]
    public async Task<IActionResult> StartLocalEngine([FromQuery] string? bridgeUrl, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return BadRequest("The local Avatar Engine is supported only on Windows.");
        }

        if (!TryGetLocalBridgePort(bridgeUrl, out var port))
        {
            return BadRequest("BridgeUrl must be an absolute loopback HTTP URL.");
        }

        var engineDirectory = LocalEngineDirectory;
        if (!HasLocalEngineExecutable(engineDirectory))
        {
            return NotFound("The local Avatar Engine executable is not installed.");
        }

        try
        {
            if (avatarOptions.Operations is { } managed)
            {
                // Managed Settings starts share the updater's validated process descriptor and lifecycle lease.
                await managed.StartAsync("0.0.0.0", port, cancellationToken).ConfigureAwait(false);
                return NoContent();
            }

            using var process = Process.Start(CreateLocalEngineStartInfo(engineDirectory, port))
                ?? throw new InvalidOperationException("The Avatar Engine process could not be started.");

            if (process.HasExited)
            {
                _logger.LogError("Local Avatar Engine exited immediately with code {ExitCode}", process.ExitCode);
                return Problem("The Avatar Engine exited immediately after starting.");
            }

            _logger.LogDebug("Started local Avatar Engine on port {Port}", port);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start local Avatar Engine on port {Port}", port);
            return Problem("An unexpected error occurred while starting the Avatar Engine.");
        }
    }

    internal static bool HasLocalEngineExecutable(string engineDirectory) =>
        System.IO.File.Exists(Path.Combine(engineDirectory, LocalEngineExecutableName));

    internal static bool TryGetLocalBridgePort(string? bridgeUrl, out int port)
    {
        if (!Uri.TryCreate(bridgeUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp ||
            (!uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) && uri.Host != "127.0.0.1"))
        {
            port = 0;
            return false;
        }

        port = uri.Port;
        return port is >= 1 and <= 65535;
    }

    internal static ProcessStartInfo CreateLocalEngineStartInfo(string engineDirectory, int port)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(engineDirectory, LocalEngineExecutableName),
            WorkingDirectory = engineDirectory,
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add("-BridgeAddress=0.0.0.0");
        startInfo.ArgumentList.Add($"-BridgePort={port}");
        startInfo.ArgumentList.Add("-windowed");
        startInfo.ArgumentList.Add($"-ResX={AvatarSettings.DefaultResolutionWidth}");
        startInfo.ArgumentList.Add($"-ResY={AvatarSettings.DefaultResolutionHeight}");
        startInfo.ArgumentList.Add("-WinX=100");
        startInfo.ArgumentList.Add("-WinY=100");
        return startInfo;
    }

    [HttpGet("settings")]
    public async Task<ActionResult<AvatarSettings.Snapshot>> GetSettings()
    {
        try
        {
            return Ok(await _avatarRuntimeSettingsService.GetSettingsAsync());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load avatar runtime settings");
            return Problem("An unexpected error occurred while loading avatar settings.");
        }
    }

    [HttpPut("settings")]
    public async Task<IActionResult> SaveSettings([FromBody] AvatarSettings.Snapshot request)
    {
        try
        {
            await _avatarRuntimeSettingsService.SaveSettingsAsync(request);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save avatar runtime settings");
            return Problem("An unexpected error occurred while saving avatar settings.");
        }
    }

    [HttpPost("sync")]
    public async Task<ActionResult<AvatarRuntimeSyncResponse>> ApplySavedSettings()
    {
        try
        {
            var result = await _avatarRuntimeSettingsService.ApplySavedSettingsAsync();
            return result.Success ? Ok(result) : BadRequest(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply saved avatar settings");
            return Problem("An unexpected error occurred while applying avatar settings.");
        }
    }

    [HttpPut("settings/connection")]
    public async Task<IActionResult> SaveConnectionSettings(
        [FromQuery] string baseUrl,
        [FromQuery] AvatarAccessMode accessMode)
    {
        if (!IsValidBridgeUrl(baseUrl) || !Enum.IsDefined(accessMode))
        {
            return BadRequest("BaseUrl and a valid accessMode are required.");
        }

        try
        {
            await _avatarRuntimeSettingsService.SaveConnectionSettingsAsync(baseUrl, accessMode);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save Avatar Bridge connection settings");
            return Problem("An unexpected error occurred while saving Avatar Bridge connection settings.");
        }
    }

    [HttpPost("pairing/start")]
    public async Task<ActionResult<AvatarPairingStartResult>> StartPairing([FromQuery] string baseUrl)
    {
        if (!IsValidBridgeUrl(baseUrl))
        {
            return BadRequest("BaseUrl must be an absolute HTTP or HTTPS URL.");
        }

        return Ok(await _avatarBridgeService.StartPairingAsync(baseUrl));
    }

    [HttpPost("pairing/complete")]
    public async Task<ActionResult<AvatarBridgeOperationResult>> CompletePairing(
        [FromBody] AvatarPairingCompleteRequest request)
    {
        if (!IsValidBridgeUrl(request.BaseUrl) ||
            string.IsNullOrEmpty(request.PairingCode) ||
            request.PairingCode.Length != 6 ||
            request.PairingCode.Any(character => character is < '0' or > '9'))
        {
            return BadRequest("BaseUrl and a six-digit PairingCode are required.");
        }

        return Ok(await _avatarBridgeService.CompletePairingAsync(request.BaseUrl, request.PairingCode));
    }

    private static bool IsValidBridgeUrl(string? baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    [HttpGet("health")]
    public async Task<ActionResult<AvatarHealthCheckResponse>> HealthCheck()
    {
        try
        {
            var result = await _avatarBridgeService.CheckHealthAsync();
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Avatar health check failed");
            return Problem("An unexpected error occurred during avatar health check.");
        }
    }

    [HttpGet("avatars")]
    public async Task<ActionResult<AvatarListResponse>> ListAvatars()
    {
        try
        {
            var result = await _avatarBridgeService.ListAvatarsAsync();
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list avatars");
            return Problem("An unexpected error occurred while retrieving available avatars.");
        }
    }

    [HttpPost("avatars/select")]
    public async Task<ActionResult<AvatarSelectResponse>> SelectAvatar([FromBody] AvatarSelectRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.AvatarName))
        {
            return BadRequest("AvatarName is required.");
        }

        try
        {
            var result = await _avatarBridgeService.SelectAvatarAsync(request.AvatarName);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to select avatar {AvatarName}", request.AvatarName);
            return Problem("An unexpected error occurred while selecting the avatar.");
        }
    }

    [HttpGet("hair")]
    public async Task<ActionResult<AvatarHairListResponse>> ListHairOptions([FromQuery] string avatarName)
    {
        if (string.IsNullOrWhiteSpace(avatarName))
        {
            return BadRequest("avatarName is required.");
        }

        try
        {
            var result = await _avatarBridgeService.ListHairOptionsAsync(avatarName);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list hair options for avatar {AvatarName}", avatarName);
            return Problem("An unexpected error occurred while retrieving available hair options.");
        }
    }

    [HttpPost("hair/select")]
    public async Task<ActionResult<AvatarHairSelectResponse>> SelectHair([FromBody] AvatarHairSelectRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.AvatarName) || string.IsNullOrWhiteSpace(request.HairId))
        {
            return BadRequest("AvatarName and HairId are required.");
        }

        try
        {
            var result = await _avatarBridgeService.SelectHairAsync(request.AvatarName, request.HairId);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to select hair {HairId} for avatar {AvatarName}", request.HairId, request.AvatarName);
            return Problem("An unexpected error occurred while selecting avatar hair.");
        }
    }

    [HttpGet("clothing")]
    public async Task<ActionResult<AvatarClothingListResponse>> ListClothingOptions([FromQuery] string avatarName)
    {
        if (string.IsNullOrWhiteSpace(avatarName))
        {
            return BadRequest("avatarName is required.");
        }

        try
        {
            var result = await _avatarBridgeService.ListClothingOptionsAsync(avatarName);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list clothing options for avatar {AvatarName}", avatarName);
            return Problem("An unexpected error occurred while retrieving available clothing options.");
        }
    }

    [HttpPost("clothing/select")]
    public async Task<ActionResult<AvatarClothingSelectResponse>> SelectClothing([FromBody] AvatarClothingSelectRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.AvatarName) || string.IsNullOrWhiteSpace(request.Slot) || string.IsNullOrWhiteSpace(request.ClothingId))
        {
            return BadRequest("AvatarName, Slot, and ClothingId are required.");
        }

        try
        {
            var result = await _avatarBridgeService.SelectClothingAsync(request.AvatarName, request.Slot, request.ClothingId);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to select clothing {ClothingId} for avatar {AvatarName} and slot {Slot}", request.ClothingId, request.AvatarName, request.Slot);
            return Problem("An unexpected error occurred while selecting avatar clothing.");
        }
    }

    [HttpGet("emotions")]
    public async Task<ActionResult<AvatarEmotionListResponse>> ListEmotions()
    {
        try
        {
            var result = await _avatarBridgeService.ListEmotionsAsync();
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list emotions");
            return Problem("An unexpected error occurred while retrieving available emotions.");
        }
    }

    [HttpPost("emotions/set")]
    public async Task<ActionResult<AvatarEmotionResponse>> SetEmotion([FromBody] AvatarEmotionRequest request)
    {
        try
        {
            var result = await _avatarBridgeService.SetEmotionAsync(request.EmotionName, request.BlendSpeed, request.Intensity);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set emotion {EmotionName}", request.EmotionName);
            return Problem("An unexpected error occurred while setting the emotion.");
        }
    }

    /// <summary>Synthesizes and queues speech for the authenticated operator.</summary>
    [HttpPost("speech/speak")]
    public async Task<IActionResult> Speak([FromBody] AvatarSpeakRequest request, CancellationToken ct)
    {
        // The MCP process must address the same installation that owns its Avatar settings.
        if (request.ExpectedAppDataPath is { } expectedPath)
        {
            try
            {
                if (!Path.IsPathFullyQualified(expectedPath))
                {
                    return BadRequest(AvatarBridgeEndpointResult.Failure("invalid_path", "ExpectedAppDataPath must be an absolute path.").Payload);
                }

                if (!PathHelper.ArePathsEqual(expectedPath, PathUtil.AppDataPath))
                {
                    return Conflict(AvatarBridgeEndpointResult.Failure("host_mismatch", "Start Assistant and Avatar MCP with the same app-data directory.").Payload);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            {
                return BadRequest(AvatarBridgeEndpointResult.Failure("invalid_path", "ExpectedAppDataPath is invalid.").Payload);
            }
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        var result = await speech.SpeakAsync(userId, request.Text, ct);
        var code = result.Payload.TryGetProperty("errorCode", out var error) && error.ValueKind == JsonValueKind.String
            ? error.GetString() : null;
        return code switch
        {
            "invalid_text" => BadRequest(result.Payload),
            "operator_required" => StatusCode(StatusCodes.Status403Forbidden, result.Payload),
            _ => Ok(result.Payload)
        };
    }

    [HttpPost("speech/queue")]
    public async Task<ActionResult<AvatarSpeechQueueResponse>> QueueSpeech([FromBody] AvatarSpeechQueueRequest request, [FromQuery] string? baseUrl = null)
    {
        if (string.IsNullOrWhiteSpace(request.Url))
        {
            return BadRequest(new AvatarSpeechQueueResponse
            {
                Success = false,
                Description = "Url is required."
            });
        }

        // Only forward URLs that point at this app's synthesized speech clips.
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) ||
            !parsed.AbsolutePath.StartsWith("/tts/", StringComparison.Ordinal))
        {
            return BadRequest(new AvatarSpeechQueueResponse
            {
                Success = false,
                Description = "Url must be an absolute http(s) URL pointing at a /tts/ clip."
            });
        }

        try
        {
            if (!await _avatarRuntimeSettingsService.EnsureReadyAsync())
            {
                return BadRequest(new AvatarSpeechQueueResponse
                {
                    Success = false,
                    Description = "Avatar bridge is not connected or saved settings could not be applied."
                });
            }

            var result = await _avatarBridgeService.QueueSpeechAsync(request.Url, request.Message, baseUrl);
            if (!result.Success)
            {
                _avatarRuntimeSettingsService.MarkUnready();
            }

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to queue avatar speech from URL");
            return Problem("An unexpected error occurred while queueing avatar speech.");
        }
    }

    [HttpPost("speech/queue-file")]
    public async Task<ActionResult<AvatarSpeechQueueFileResponse>> QueueFileSpeech([FromBody] AvatarSpeechQueueFileRequest request)
    {
        try
        {
            var result = await _avatarBridgeService.QueueFileSpeechAsync(request.FileName, request.Message);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to queue avatar speech from file");
            return Problem("An unexpected error occurred while queueing avatar speech.");
        }
    }

    [HttpPost("speech/stop")]
    public async Task<ActionResult<AvatarSpeechStopResponse>> StopSpeech([FromBody] AvatarSpeechStopRequest? request, [FromQuery] string? baseUrl = null)
    {
        try
        {
            var result = await _avatarBridgeService.StopSpeechAsync(request?.ForcedStop ?? false, baseUrl);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop avatar speech");
            return Problem("An unexpected error occurred while stopping avatar speech.");
        }
    }

    [HttpPost("speech/volume")]
    public async Task<ActionResult<AvatarSpeechVolumeResponse>> SetSpeechVolume([FromBody] AvatarSpeechVolumeRequest request)
    {
        try
        {
            var result = await _avatarBridgeService.SetSpeechVolumeAsync(request.Volume);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set speech volume");
            return Problem("An unexpected error occurred while setting speech volume.");
        }
    }

    [HttpPost("resolution")]
    public async Task<ActionResult<AvatarResolutionResponse>> SetResolution([FromBody] AvatarResolutionRequest request)
    {
        try
        {
            var result = await _avatarBridgeService.ChangeResolutionAsync(request.Width, request.Height, request.Fullscreen);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set resolution");
            return Problem("An unexpected error occurred while setting the resolution.");
        }
    }

    [HttpPost("background/color")]
    public async Task<ActionResult<AvatarBackgroundColorResponse>> SetBackgroundColor([FromBody] AvatarBackgroundColorRequest request)
    {
        try
        {
            var result = await _avatarBridgeService.ChangeBackgroundColorAsync(request.Red, request.Green, request.Blue);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set background color");
            return Problem("An unexpected error occurred while setting the background color.");
        }
    }

    /// <summary>Applies both layouts and their runtime selection mode.</summary>
    [HttpPost("presentation/settings")]
    public async Task<ActionResult<AvatarPresentationSettingsResponse>> SetPresentationSettings([FromBody] AvatarPresentationSettingsRequest request)
    {
        try
        {
            return Ok(await _avatarBridgeService.SetPresentationSettingsAsync(request));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply avatar presentation settings");
            return Problem("An unexpected error occurred while applying avatar presentation settings.");
        }
    }

    [HttpPost("message/text")]
    public async Task<ActionResult<AvatarMessageTextResponse>> ShowMessage([FromBody] AvatarMessageTextRequest request)
    {
        try
        {
            var result = await _avatarBridgeService.ShowMessageAsync(request.Message);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show avatar message text");
            return Problem("An unexpected error occurred while showing avatar message text.");
        }
    }

    [HttpPost("message/clear")]
    public async Task<ActionResult<AvatarMessageClearResponse>> ClearMessage()
    {
        try
        {
            var result = await _avatarBridgeService.ClearMessageAsync();
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear avatar message text");
            return Problem("An unexpected error occurred while clearing avatar message text.");
        }
    }

    [HttpPost("graphics/preset")]
    public async Task<ActionResult<AvatarGraphicsPresetResponse>> SetGraphicsPreset([FromBody] AvatarGraphicsPresetRequest request)
    {
        try
        {
            var result = await _avatarBridgeService.SetGraphicsPresetAsync(request.Level, request.RayTracingEnabled);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set graphics preset");
            return Problem("An unexpected error occurred while setting the graphics preset.");
        }
    }

    [HttpPost("shutdown")]
    public async Task<ActionResult<AvatarShutdownResponse>> Shutdown([FromQuery] string expectedBaseUrl, [FromBody] AvatarShutdownRequest? request)
    {
        try
        {
            var result = await _avatarBridgeService.ShutdownAsync(expectedBaseUrl, request?.ForceShutdown ?? false);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to shutdown avatar engine");
            return Problem("An unexpected error occurred while shutting down the avatar engine.");
        }
    }
}
