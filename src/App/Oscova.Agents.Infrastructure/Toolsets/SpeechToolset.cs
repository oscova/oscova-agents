using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Speech;
using Oscova.Agents.Harness;
using Oscova.Agents.Infrastructure.Speech;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Speech;

namespace Oscova.Agents.Infrastructure.Toolsets;

/// <summary>Lets an agent select the words spoken through the user's configured chat output.</summary>
[DisplayName("Speech")]
public sealed class SpeechToolset(UserServiceManager users, RunContextAccessor runs,
    SpeechSynthesisRouter router, SpeechClipSynthesizer synthesizer, ILogger<SpeechToolset> logger) : IAgentToolset
{
    /// <summary>Prepares speech for the active web conversation without enabling automatic read-aloud.</summary>
    [DisplayName("speak")]
    [AgentTool]
    [Description("""
        Speak the supplied text using the user's selected speech provider, voice, and output destination.
        Text must contain nonblank speakable content.
        Requires an authenticated active web conversation.
        Requires the directly selected chat agent, delegated agents cannot deliver speech.
        Requires an available provider configured in Speech settings.
        Local-only execution requires a local provider endpoint.
        Works independently of the composer's Read aloud button.
        Success means audio prepared for delivery, not confirmed audible playback.
        """)]
    public async Task<string> SpeakAsync(
        [Description("Text to speak. Must contain nonblank speakable content.")] string text,
        CancellationToken cancellationToken = default)
    {
        var message = SpeechOutputPolicy.SanitizeMarkdownForSpeech(text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ToolFailureException("text must contain nonblank speakable content. Supply the words to say.");
        }

        var run = runs.Current;
        if (run is null || run.ExecutionKind != RunExecutionKind.WebChat ||
            string.IsNullOrWhiteSpace(run.UserId) || string.IsNullOrWhiteSpace(run.ExecutionId) ||
            run.Elicitation?.Channel != ElicitationChannel.WebUI)
        {
            throw new ToolFailureException("Speech requires an authenticated active web conversation. Run this tool from the web chat.");
        }

        if (run.DelegationAgentIds.Count > 0)
        {
            throw new ToolFailureException("Delegated agents cannot deliver speech to the web chat. Invoke Speech from the directly selected chat agent.");
        }

        try
        {
            var scope = await users.GetOrCreateScopeAsync(run.UserId, cancellationToken).ConfigureAwait(false);
            var config = scope.SpeechSettings.SelectedSynthesisProvider?.Clone()
                ?? throw new ToolFailureException("No speech synthesis provider is selected. Select one in Speech settings.");
            var voice = scope.SpeechSettings.SelectedVoiceId;
            var targets = SpeechOutputPolicy.ResolveTargets(scope.IsInRole("Admin"), scope.SpeechSettings.AssistantSpeechOutputMode);
            var localOnly = AgentRunScopeFactory.RequiresLocalExecution(run.AirGapped, scope.PrivacySettings.IsLocalOnly, false);
            var apiKey = string.IsNullOrWhiteSpace(config.CredentialKey) ? null
                : await scope.CredentialService.ResolveFieldAsync(config.CredentialKey, cancellationToken).ConfigureAwait(false);
            if (!await router.IsAvailableAsync(config, apiKey, localOnly, cancellationToken).ConfigureAwait(false))
            {
                throw new ToolFailureException("The selected speech provider is unavailable. Check its model and credential in Speech settings.");
            }

            var audio = await synthesizer.SynthesizeAsync(run.UserId, config, apiKey, voice, message, localOnly, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(audio.FileUrl))
            {
                throw new ToolFailureException("No playable speech clip was prepared. Check the selected provider and audio cache permissions.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            // The matching live panel consumes this compact receipt, audio bytes stay out of the transcript.
            return JsonSerializer.Serialize(new SpeechPlaybackReceipt(run.ExecutionId, message, audio.FileUrl,
                SpeechOutputPolicy.GetClipDurationMs(audio), targets));
        }
        catch (Exception ex) when (ex is not ToolFailureException and not OperationCanceledException)
        {
            logger.LogError(ex, "Explicit speech preparation failed for user {UserId}", run.UserId);
            throw new ToolFailureException("Speech preparation failed. Check the selected provider, its credential, and local-only restrictions in Speech settings.");
        }
    }
}
