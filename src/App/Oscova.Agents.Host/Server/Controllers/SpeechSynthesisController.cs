using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oscova.Agents.Core.ApiContracts.Speech;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Secrets;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Core.Speech;
using Oscova.Agents.Speech;
using Oscova.Agents.Speech.Contracts;
using Oscova.Agents.Infrastructure.Speech;
using Oscova.Agents.Infrastructure.Users;

namespace Oscova.Agents.Host.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class SpeechSynthesisController(
    UserServiceManager userServiceManager,
    SpeechSynthesisRouter synthService,
    SpeechClipStore clipStore,
    ISpeechProviderCatalog speechCatalog,
    IDownloadService downloads,
    ModelDownloadSettings modelDownloadSettings,
    RunContextAccessor runContextAccessor,
    ILogger<SpeechSynthesisController> logger) : UserScopeControllerBase(userServiceManager)
{
    private readonly SpeechSynthesisRouter _synthService = synthService;
    private readonly SpeechClipStore _clipStore = clipStore;
    private readonly IDownloadService _downloads = downloads;
    private readonly ModelDownloadSettings _modelDownloadSettings = modelDownloadSettings;
    private readonly RunContextAccessor _runContextAccessor = runContextAccessor;
    private readonly ILogger<SpeechSynthesisController> _logger = logger;

    [HttpPost("start/{sessionId}")]
    public async Task<IActionResult> Start([FromRoute] string sessionId, [FromBody] SpeechSynthesisRequest? req)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return BadRequest("sessionId required.");
        }

        try
        {
            var userScope = await GetUserScopeAsync(HttpContext.RequestAborted);
            var config = GetSynthesisConfig(userScope.SpeechSettings);
            var apiKey = await ResolveApiKeyAsync(config, userScope.CredentialService, HttpContext.RequestAborted);
            await using var requestContext = _runContextAccessor.SetUserRequestContext(userScope.UserId, HttpContext.TraceIdentifier);
            await _synthService.StartSessionAsync(
                config,
                apiKey,
                GetScopedSessionId(userScope.UserId, sessionId),
                req?.VoiceId,
                RequiresLocalEndpoint(userScope),
                HttpContext.RequestAborted);
            return Ok(new { sessionId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start speech synthesis session {SessionId}", sessionId);
            return Problem("An unexpected error occurred while starting the speech synthesis session.");
        }
    }

    [HttpPost("synthesize/{sessionId}")]
    public async Task<ActionResult<SpeechSynthesisResponse>> Synthesize([FromRoute] string sessionId, [FromBody] SpeechSynthesisRequest req)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return BadRequest("sessionId required.");
        }

        req.Text = SpeechOutputPolicy.SanitizeMarkdownForSpeech(req.Text);
        if (string.IsNullOrWhiteSpace(req.Text))
        {
            return BadRequest("text required.");
        }

        try
        {
            req.IncludeWavHeader = true;
            var userScope = await GetUserScopeAsync(HttpContext.RequestAborted);
            var config = GetSynthesisConfig(userScope.SpeechSettings);
            var apiKey = await ResolveApiKeyAsync(config, userScope.CredentialService, HttpContext.RequestAborted);
            var scopedSessionId = GetScopedSessionId(userScope.UserId, sessionId);
            await using var requestContext = _runContextAccessor.SetUserRequestContext(userScope.UserId, HttpContext.TraceIdentifier);
            var response = await _synthService.SynthesizeAsync(
                config,
                apiKey,
                scopedSessionId,
                req,
                RequiresLocalEndpoint(userScope),
                HttpContext.RequestAborted);
            if (string.IsNullOrWhiteSpace(response.FileUrl))
            {
                // Providers produce audio; the Host owns browser-served clip storage and URLs.
                response.FileUrl = await _clipStore.SaveAsync(
                    scopedSessionId,
                    response.Audio,
                    HttpContext.RequestAborted);
            }

            return Ok(response);
        }
        catch (SpeechModelUnavailableException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (SpeechSynthesisSessionUnavailableException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to synthesize speech for session {SessionId}", sessionId);
            return Problem("An unexpected error occurred while synthesizing speech.");
        }
    }

    [HttpPost("stop/{sessionId}")]
    public async Task<IActionResult> Stop([FromRoute] string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return BadRequest("sessionId required.");
        }

        try
        {
            var userScope = await GetUserScopeAsync(HttpContext.RequestAborted);
            var config = GetSynthesisConfig(userScope.SpeechSettings);
            await _synthService.StopSessionAsync(
                config,
                GetScopedSessionId(userScope.UserId, sessionId),
                RequiresLocalEndpoint(userScope),
                HttpContext.RequestAborted);
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop speech synthesis session {SessionId}", sessionId);
            return Problem("An unexpected error occurred while stopping the speech synthesis session.");
        }
    }

    [HttpDelete("cache/{sessionId}")]
    public async Task<IActionResult> ClearCache([FromRoute] string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return BadRequest("sessionId required.");
        }

        try
        {
            var userScope = await GetUserScopeAsync(HttpContext.RequestAborted);
            await _clipStore.ClearAsync(
                GetScopedSessionId(userScope.UserId, sessionId),
                HttpContext.RequestAborted);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear speech synthesis cache for session {SessionId}", sessionId);
            return Problem("An error occurred while clearing the speech synthesis cache.");
        }
    }

    private static string GetScopedSessionId(string userId, string sessionId) => $"{userId}:{sessionId}";

    [HttpGet("available")]
    public async Task<ActionResult<bool>> IsAvailable(CancellationToken ct)
    {
        try
        {
            var userScope = await GetUserScopeAsync(ct);
            var config = userScope.SpeechSettings.SelectedSynthesisProvider
                ?? userScope.SpeechSettings.SynthesisProviderConfigs.FirstOrDefault();
            if (config is null || !speechCatalog.Synthesis.Any(provider => provider.Type == config.Type))
            {
                return Ok(false);
            }

            var apiKey = await ResolveApiKeyAsync(config, userScope.CredentialService, ct);
            var available = await _synthService.IsAvailableAsync(config, apiKey, RequiresLocalEndpoint(userScope), ct);
            return Ok(available);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to determine speech synthesis availability");
            return Problem("An unexpected error occurred while determining speech synthesis availability.");
        }
    }

    [HttpPost("download")]
    public async Task<IActionResult> StartDownload(CancellationToken ct)
    {
        try
        {
            var userScope = await GetUserScopeAsync(ct);
            var config = GetSynthesisConfig(userScope.SpeechSettings);
            var overrideUrl = config.Type == SpeechProviderKeys.Local ? _modelDownloadSettings.TtsModelUrl : null;
            await _synthService.StartDownloadAsync(config, overrideUrl, RequiresLocalEndpoint(userScope), ct);
            return Accepted();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start speech synthesis model download.");
            return Problem("An unexpected error occurred while starting the model download.");
        }
    }

    [HttpPost("cancel")]
    public async Task<IActionResult> CancelDownload(CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);
        var config = userScope.SpeechSettings.SelectedSynthesisProvider
            ?? userScope.SpeechSettings.SynthesisProviderConfigs.FirstOrDefault();
        if (config is null || !speechCatalog.Synthesis.Any(provider => provider.Type == config.Type))
        {
            return NoContent();
        }

        var modelPath = _synthService.GetDownloadModelPath(config, RequiresLocalEndpoint(userScope));
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
        var config = userScope.SpeechSettings.SelectedSynthesisProvider
            ?? userScope.SpeechSettings.SynthesisProviderConfigs.FirstOrDefault();
        if (config is null || !speechCatalog.Synthesis.Any(provider => provider.Type == config.Type))
        {
            return NoContent();
        }

        var modelPath = _synthService.GetDownloadModelPath(config, RequiresLocalEndpoint(userScope));
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

    [HttpGet("voices")]
    public async Task<ActionResult<IReadOnlyList<SpeechVoiceSummary>>> GetVoices(CancellationToken ct)
    {
        try
        {
            var userScope = await GetUserScopeAsync(ct);
            var config = GetSynthesisConfig(userScope.SpeechSettings);
            var apiKey = await ResolveApiKeyAsync(config, userScope.CredentialService, ct);
            var voices = await _synthService.GetAvailableVoicesAsync(config, apiKey, RequiresLocalEndpoint(userScope), ct);
            return Ok(ToVoiceSummaries(voices));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve available voices");
            return Problem("An unexpected error occurred while retrieving available voices.");
        }
    }

    /// <summary>
    /// Retrieves available voices for an unsaved synthesis provider draft.
    /// </summary>
    [HttpPost("voices/preview")]
    public async Task<ActionResult<IReadOnlyList<SpeechVoiceSummary>>> PreviewVoices([FromBody] SpeechVoicePreviewRequest? request, CancellationToken ct)
    {
        if (request is null)
        {
            return BadRequest("request required.");
        }

        if (string.IsNullOrWhiteSpace(request.Type))
        {
            return BadRequest("provider type required.");
        }

        try
        {
            var userScope = await GetUserScopeAsync(ct);
            var config = new SpeechProviderConfig
            {
                Type = request.Type,
                BaseUrl = request.BaseUrl,
                ModelId = request.ModelId,
                CredentialKey = request.CredentialKey,
                VoiceId = request.VoiceId
            };
            var apiKey = await ResolveApiKeyAsync(config, userScope.CredentialService, ct);
            var voices = await _synthService.GetAvailableVoicesAsync(config, apiKey, RequiresLocalEndpoint(userScope), ct);
            return Ok(ToVoiceSummaries(voices));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to preview voices for speech provider {ProviderType}", request.Type);
            return Problem("An unexpected error occurred while previewing available voices.");
        }
    }

    private bool RequiresLocalEndpoint(UserServiceScope userScope) => AgentRunScopeFactory.RequiresLocalExecution(
        _runContextAccessor.Current?.AirGapped == true,
        userScope.PrivacySettings.IsLocalOnly,
        agentLocalOnly: false);

    private static SpeechProviderConfig GetSynthesisConfig(SpeechSettings settings)
    {
        return settings.SelectedSynthesisProvider ?? settings.SynthesisProviderConfigs.FirstOrDefault()
            ?? throw new InvalidOperationException("No speech synthesis provider is configured.");
    }

    private static IReadOnlyList<SpeechVoiceSummary> ToVoiceSummaries(IEnumerable<SpeechSynthesisVoice> voices) =>
        [.. voices.Select(voice => new SpeechVoiceSummary
        {
            Id = voice.Id,
            DisplayName = voice.DisplayName,
            Language = voice.Language,
            Gender = voice.Gender
        })];

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
