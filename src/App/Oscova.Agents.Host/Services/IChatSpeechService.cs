using Oscova.Agents.Core.ApiContracts.Speech;
using Oscova.Agents.Speech;
using Oscova.Agents.Speech.Contracts;

namespace Oscova.Agents.Host.Services;

public interface IChatSpeechService
{
    Task<bool> StartSessionAsync(string sessionId, string? voiceId);
    Task StopSessionAsync(string sessionId);
    Task ClearCacheAsync(string sessionId);
    Task<SpeechSynthesisResponse?> SynthesizeAsync(string sessionId, SpeechSynthesisRequest request);
    Task<SpeechTranscription?> TranscribeAsync(string base64Wav, string language);
    Task<bool> IsTtsAvailableAsync();
    Task<bool> IsAsrAvailableAsync();
    Task StartTtsModelDownloadAsync();
    Task StartAsrModelDownloadAsync();
    Task CancelTtsModelDownloadAsync();
    Task CancelAsrModelDownloadAsync();
    Task<ModelDownloadStatus?> GetTtsModelProgressAsync();
    Task<ModelDownloadStatus?> GetAsrModelProgressAsync();
}
