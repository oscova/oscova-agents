using Oscova.Agents.Core.ApiContracts.Speech;
using Oscova.Agents.Speech;
using Oscova.Agents.Speech.Contracts;
using Oscova.Agents.Host.Constants;

namespace Oscova.Agents.Host.Services;

public class ChatSpeechService(HttpClient http, ILogger<ChatSpeechService> logger) : IChatSpeechService
{
    public async Task<bool> StartSessionAsync(string sessionId, string? voiceId)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(
                ApiRoutes.SpeechSynthesis.Start(sessionId),
                new SpeechSynthesisRequest { VoiceId = voiceId }).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var reason = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                logger.LogError("Failed to start speech synthesis session: {Status} {Reason}", response.StatusCode, reason);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start speech synthesis session {SessionId}.", sessionId);
            return false;
        }
    }

    public async Task StopSessionAsync(string sessionId)
    {
        try
        {
            using var response = await http.PostAsync(ApiRoutes.SpeechSynthesis.Stop(sessionId), null).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Failed to stop speech synthesis session: {Status}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to stop speech synthesis session {SessionId}.", sessionId);
        }
    }

    public async Task ClearCacheAsync(string sessionId)
    {
        try
        {
            using var response = await http.DeleteAsync(ApiRoutes.SpeechSynthesis.Cache(sessionId)).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Failed to clear speech synthesis cache: {Status}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clear speech synthesis cache for session {SessionId}.", sessionId);
        }
    }

    public async Task<SpeechSynthesisResponse?> SynthesizeAsync(string sessionId, SpeechSynthesisRequest request)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(ApiRoutes.SpeechSynthesis.Synthesize(sessionId), request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var reason = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                logger.LogError("Speech synthesis request failed: {Status} {Reason}", response.StatusCode, reason);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<SpeechSynthesisResponse>().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to synthesize speech for session {SessionId}.", sessionId);
            return null;
        }
    }

    public async Task<SpeechTranscription?> TranscribeAsync(string base64Wav, string language)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(ApiRoutes.SpeechRecognition.Transcribe, new
            {
                Base64Wav = base64Wav,
                Language = language
            }).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var reason = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                logger.LogError("Speech transcription request failed: {Status} {Reason}", response.StatusCode, reason);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<SpeechTranscription>().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to transcribe audio.");
            return null;
        }
    }

    public async Task<bool> IsTtsAvailableAsync()
    {
        return await FetchAvailabilityAsync(ApiRoutes.SpeechSynthesis.Available).ConfigureAwait(false);
    }

    public async Task<bool> IsAsrAvailableAsync()
    {
        return await FetchAvailabilityAsync(ApiRoutes.SpeechRecognition.Available).ConfigureAwait(false);
    }

    public Task StartTtsModelDownloadAsync() => StartModelDownloadAsync(ApiRoutes.SpeechSynthesis.Download);

    public Task StartAsrModelDownloadAsync() => StartModelDownloadAsync(ApiRoutes.SpeechRecognition.Download);

    public Task CancelTtsModelDownloadAsync() => CancelModelDownloadAsync(ApiRoutes.SpeechSynthesis.Cancel);

    public Task CancelAsrModelDownloadAsync() => CancelModelDownloadAsync(ApiRoutes.SpeechRecognition.Cancel);

    public Task<ModelDownloadStatus?> GetTtsModelProgressAsync() => FetchProgressAsync(ApiRoutes.SpeechSynthesis.Progress);

    public Task<ModelDownloadStatus?> GetAsrModelProgressAsync() => FetchProgressAsync(ApiRoutes.SpeechRecognition.Progress);

    private async Task CancelModelDownloadAsync(string endpoint)
    {
        try
        {
            using var response = await http.PostAsync(endpoint, null).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Failed to cancel model download via {Endpoint}: {Status}", endpoint, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to cancel model download via {Endpoint}.", endpoint);
        }
    }

    private async Task StartModelDownloadAsync(string endpoint)
    {
        try
        {
            using var response = await http.PostAsync(endpoint, null).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Failed to start model download via {Endpoint}: {Status}", endpoint, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start model download via {Endpoint}.", endpoint);
        }
    }

    private async Task<ModelDownloadStatus?> FetchProgressAsync(string endpoint)
    {
        try
        {
            using var response = await http.GetAsync(endpoint).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<ModelDownloadStatus>().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch model download progress from {Endpoint}.", endpoint);
            return null;
        }
    }

    private async Task<bool> FetchAvailabilityAsync(string endpoint)
    {
        try
        {
            return await http.GetFromJsonAsync<bool>(endpoint).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch availability from {Endpoint}.", endpoint);
            return false;
        }
    }
}
