using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oscova.Agents.Core.ApiContracts.Speech;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Secrets;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Speech;
using Oscova.Agents.Infrastructure.Users;

namespace Oscova.Agents.Host.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public sealed class SpeechRecognitionController(
    UserServiceManager userServiceManager,
    SpeechRecognitionRouter recognizer,
    ISpeechProviderCatalog speechCatalog,
    IDownloadService downloads,
    ModelDownloadSettings modelDownloadSettings,
    RunContextAccessor runContextAccessor,
    ILogger<SpeechRecognitionController> logger) : UserScopeControllerBase(userServiceManager)
{
    private readonly SpeechRecognitionRouter _recognizer = recognizer;
    private readonly IDownloadService _downloads = downloads;
    private readonly ModelDownloadSettings _modelDownloadSettings = modelDownloadSettings;
    private readonly RunContextAccessor _runContextAccessor = runContextAccessor;
    private readonly ILogger<SpeechRecognitionController> _logger = logger;

    // Keep the payload simple while the client/server boundary is being established.
    public sealed class TranscribeRequest
    {
        public string Base64Wav { get; set; } = string.Empty;
        public string Language { get; set; } = "auto";
    }

    [HttpPost("transcribe")]
    public async Task<ActionResult<SpeechTranscription>> Transcribe([FromBody] TranscribeRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Base64Wav))
        {
            return BadRequest("Base64Wav is required.");
        }

        try
        {
            var bytes = Convert.FromBase64String(request.Base64Wav);
            var userScope = await GetUserScopeAsync(ct);
            var config = GetRecognitionConfig(userScope.SpeechSettings);
            var apiKey = await ResolveApiKeyAsync(config, userScope.CredentialService, ct);
            await using var requestContext = _runContextAccessor.SetUserRequestContext(userScope.UserId, HttpContext.TraceIdentifier);
            var transcription = await _recognizer.TranscribeAsync(
                config,
                apiKey,
                bytes,
                RequiresLocalEndpoint(userScope),
                request.Language,
                ct);
            return Ok(transcription);
        }
        catch (FormatException)
        {
            return BadRequest("Invalid base64 audio payload.");
        }
        catch (SpeechModelUnavailableException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transcription failed");
            return Problem("Transcription failed.");
        }
    }

    [HttpPost("download")]
    public async Task<IActionResult> StartDownload(CancellationToken ct)
    {
        try
        {
            var userScope = await GetUserScopeAsync(ct);
            var config = GetRecognitionConfig(userScope.SpeechSettings);
            var overrideUrl = config.Type == SpeechProviderKeys.Local ? _modelDownloadSettings.AsrModelUrl : null;
            await _recognizer.StartDownloadAsync(config, overrideUrl, RequiresLocalEndpoint(userScope), ct);
            return Accepted();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start speech recognition model download.");
            return Problem("An unexpected error occurred while starting the model download.");
        }
    }

    [HttpPost("cancel")]
    public async Task<IActionResult> CancelDownload(CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);
        var config = userScope.SpeechSettings.SelectedRecognitionProvider
            ?? userScope.SpeechSettings.RecognitionProviderConfigs.FirstOrDefault();
        if (config is null || !speechCatalog.Recognition.Any(provider => provider.Type == config.Type))
        {
            return NoContent();
        }

        var modelPath = _recognizer.GetDownloadModelPath(config, RequiresLocalEndpoint(userScope));
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            return NoContent();
        }

        var cancelled = _downloads.TryCancel(modelPath);
        return cancelled ? Accepted() : NoContent();
    }

    [HttpGet("progress")]
    public async Task<ActionResult<ModelDownloadStatus>> GetProgress(CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);
        var config = userScope.SpeechSettings.SelectedRecognitionProvider
            ?? userScope.SpeechSettings.RecognitionProviderConfigs.FirstOrDefault();
        if (config is null || !speechCatalog.Recognition.Any(provider => provider.Type == config.Type))
        {
            return NoContent();
        }

        var modelPath = _recognizer.GetDownloadModelPath(config, RequiresLocalEndpoint(userScope));
        if (string.IsNullOrWhiteSpace(modelPath) || !_downloads.TryGet(modelPath, out var info) || info is null)
        {
            return NoContent();
        }

        return Ok(new ModelDownloadStatus
        {
            Status = info.Status,
            BytesReceived = info.BytesReceived,
            TotalBytes = info.TotalBytes,
            Percent = info.Percent,
            Error = info.Error?.Message
        });
    }

    [HttpGet("available")]
    public async Task<ActionResult<bool>> IsAvailable(CancellationToken ct)
    {
        try
        {
            var userScope = await GetUserScopeAsync(ct);
            var config = userScope.SpeechSettings.SelectedRecognitionProvider
                ?? userScope.SpeechSettings.RecognitionProviderConfigs.FirstOrDefault();
            if (config is null || !speechCatalog.Recognition.Any(provider => provider.Type == config.Type))
            {
                return Ok(false);
            }

            var apiKey = await ResolveApiKeyAsync(config, userScope.CredentialService, ct);
            var available = await _recognizer.IsAvailableAsync(config, apiKey, RequiresLocalEndpoint(userScope), ct);
            return Ok(available);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to determine speech recognition availability");
            return Problem("An unexpected error occurred while checking speech recognition availability.");
        }
    }

    private bool RequiresLocalEndpoint(UserServiceScope userScope) => AgentRunScopeFactory.RequiresLocalExecution(
        _runContextAccessor.Current?.AirGapped == true,
        userScope.PrivacySettings.IsLocalOnly,
        agentLocalOnly: false);

    private static SpeechProviderConfig GetRecognitionConfig(SpeechSettings settings)
    {
        return settings.SelectedRecognitionProvider ?? settings.RecognitionProviderConfigs.FirstOrDefault()
            ?? throw new InvalidOperationException("No speech recognition provider is configured.");
    }

    private static Task<string?> ResolveApiKeyAsync(
        SpeechProviderConfig config,
        ICredentialService credentialService,
        CancellationToken ct)
    {
        return string.IsNullOrWhiteSpace(config.CredentialKey)
            ? Task.FromResult<string?>(null)
            : credentialService.ResolveFieldAsync(config.CredentialKey, ct);
    }
}
