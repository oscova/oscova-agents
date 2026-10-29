using Microsoft.Extensions.Logging;
using Oscova.Agents.Avatar.Contracts;
using Oscova.Agents.Core.ApiContracts.Avatar;
using Oscova.Agents.Core.Avatar;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Speech;
using Oscova.Agents.Infrastructure.Speech;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Speech;

namespace Oscova.Agents.Infrastructure.Avatar;

/// <summary>Synthesizes operator text and queues its audio on the shared avatar.</summary>
public sealed class AvatarSpeechService(
    UserServiceManager users,
    IAvatarRuntimeSettingsService runtime,
    AvatarBridgeService bridge,
    SpeechSynthesisRouter router,
    SpeechClipSynthesizer synthesizer,
    IWebHostInfoProvider hostInfo,
    RunContextAccessor runContext,
    ILogger<AvatarSpeechService> logger)
{
    /// <summary>Speaks using the operator's provider and voice from Speech settings.</summary>
    public async Task<AvatarBridgeEndpointResult> SpeakAsync(
        string userId, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return AvatarBridgeEndpointResult.Failure("operator_required", "Sign in as the operator.");
        }

        var message = SpeechOutputPolicy.RemoveEmojiTextElements(text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            return AvatarBridgeEndpointResult.Failure("invalid_text", "Avatar speech text is required.");
        }

        try
        {
            var scope = await users.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
            if (!scope.IsInRole("Admin"))
            {
                return AvatarBridgeEndpointResult.Failure("operator_required", "Sign in as the operator.");
            }

            var config = scope.SpeechSettings.SelectedSynthesisProvider?.Clone();
            if (config is null)
            {
                return AvatarBridgeEndpointResult.Failure("speech_unavailable", "Select a speech synthesis provider in Speech settings.");
            }

            var avatar = await runtime.GetSettingsAsync().ConfigureAwait(false);
            var voice = scope.SpeechSettings.SelectedVoiceId;
            var localOnly = AgentRunScopeFactory.RequiresLocalExecution(
                runContext.Current?.AirGapped == true, scope.PrivacySettings.IsLocalOnly, false);
            var apiKey = string.IsNullOrWhiteSpace(config.CredentialKey) ? null
                : await scope.CredentialService.ResolveFieldAsync(config.CredentialKey, ct).ConfigureAwait(false);
            if (!await router.IsAvailableAsync(config, apiKey, localOnly, ct).ConfigureAwait(false))
            {
                return AvatarBridgeEndpointResult.Failure("speech_unavailable", "Check the selected speech provider's model and credential. Unlock stored credentials if needed.");
            }

            // Speech needs readiness without replacing live-only Avatar changes.
            var sync = await runtime.ApplySavedSettingsAsync(forceApply: false).ConfigureAwait(false);
            if (!sync.Success)
            {
                return AvatarBridgeEndpointResult.Failure(sync.ErrorCode ?? "avatar_unavailable", sync.Description);
            }

            ct.ThrowIfCancellationRequested();

            var audio = await synthesizer.SynthesizeAsync(userId, config, apiKey, voice, message, localOnly, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(audio.FileUrl) && audio.Audio.Length == 0)
            {
                return AvatarBridgeEndpointResult.Failure("audio_unavailable", "The selected speech provider returned no audio.");
            }

            var audioUrl = hostInfo.BuildAbsoluteUrl(audio.FileUrl);
            if (string.IsNullOrWhiteSpace(audioUrl))
            {
                return AvatarBridgeEndpointResult.Failure("audio_unavailable", "Could not prepare the audio URL. Check the Assistant log and audio cache permissions.");
            }

            ct.ThrowIfCancellationRequested();

            // Pin the Engine target across synthesis because saved settings can change meanwhile.
            var result = await bridge.SendEndpointAsync(avatar.BridgeUrl, avatar.PreferredAccessMode,
                HttpMethod.Post, "/avatarbridge/speech/queue",
                new AvatarSpeechQueueRequest { Url = audioUrl, Message = message }, true, ct).ConfigureAwait(false);
            if (result.IsError)
            {
                runtime.MarkUnready();
            }

            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Avatar speech prerequisites failed for user {UserId}", userId);
            return AvatarBridgeEndpointResult.Failure("speech_unavailable",
                "Check the selected provider, model, credential, and voice in Speech settings. A local-only policy requires a local provider endpoint.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Avatar speech failed for user {UserId}", userId);
            return AvatarBridgeEndpointResult.Failure("speech_failed",
                "Avatar speech failed. Check the Assistant log and the selected provider and voice in Speech settings before retrying.");
        }
    }
}
