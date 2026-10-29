using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Speech;
using Oscova.Agents.Speech.Contracts;

namespace Oscova.Agents.Infrastructure.Speech;

/// <summary>Prepares one speech clip without sharing the browser's synthesis session.</summary>
public sealed class SpeechClipSynthesizer(
    SpeechSynthesisRouter router,
    SpeechClipStore clips,
    RunContextAccessor runContext,
    ILogger<SpeechClipSynthesizer> logger)
{
    /// <summary>Synthesizes and caches a clip while preserving the user's run restrictions.</summary>
    public async Task<SpeechSynthesisResponse> SynthesizeAsync(string userId, SpeechProviderConfig config,
        string? apiKey, string voice, string text, bool localOnly, CancellationToken ct)
    {
        var sessionId = $"speech-{Guid.NewGuid():N}";
        SpeechSynthesisResponse audio;
        try
        {
            // The provider session identifies audio state, while the run retains the consuming user.
            await using var requestContext = runContext.SetUserRequestContext(userId, sessionId);
            await router.StartSessionAsync(config, apiKey, sessionId, voice, localOnly, ct).ConfigureAwait(false);
            audio = await router.SynthesizeAsync(config, apiKey, sessionId,
                new SpeechSynthesisRequest { Text = text, VoiceId = voice, IncludeWavHeader = true },
                localOnly, ct).ConfigureAwait(false);
        }
        finally
        {
            // Cleanup survives cancellation without hiding a failed synthesis request.
            try
            {
                await router.StopSessionAsync(config, sessionId, localOnly, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Speech session cleanup failed for provider {ProviderType}", config.Type);
            }
        }

        if (string.IsNullOrWhiteSpace(audio.FileUrl) && audio.Audio.Length > 0)
        {
            audio.FileUrl = await clips.SaveAsync(sessionId, audio.Audio, ct).ConfigureAwait(false);
        }

        return audio;
    }
}
