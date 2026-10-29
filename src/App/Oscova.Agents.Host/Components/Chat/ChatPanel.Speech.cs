using System.Text.Json;
using Oscova.Agents.Infrastructure.Toolsets;
using System.Diagnostics;
using Oscova.Agents.Conversations;
using Microsoft.JSInterop;
using Oscova.Agents.Host.Localization;
using Oscova.Agents.Core.Speech;
using Oscova.Agents.Speech.Contracts;
using Oscova.Agents.Core.Utils;
using Oscova.Agents.Infrastructure.Utils;
using Oscova.Agents.Host.Services.UI;
using Oscova.Agents.Host.Services;

namespace Oscova.Agents.Host.Components.Chat;

/// <summary>
/// Partial class for ChatPanel handling all speech-related functionality:
/// TTS toggle/queue, ASR record/transcribe, avatar bridge callbacks, and voice mode.
/// </summary>
public partial class ChatPanel
{
    private sealed class TtsStreamState
    {
        public bool IsExplicit { get; init; }
        public StreamToSegmentProvider SegmentProvider { get; } = new();
        public IList<int> SegmentTextLengths { get; } = [];
        // True when avatar routing for this sequence is enabled. Cleared if EnsureReadyAsync
        // fails at sequence-start or if a mid-sequence stream POST fails.
        public bool AvatarRoutingEnabled { get; set; }
        // Null when no emotion catalog is loaded, so replies are spoken unchanged.
        public AvatarMarkupParser? MarkupParser { get; init; }
    }

    internal enum VoiceModeState
    {
        Inactive,
        Listening,
        Transcribing,
        Processing,
        Speaking
    }

    // Speech state
    private string InputPlaceholder { get; set; } = "Type your message...";
    private bool _isRecordingAudio;
    private bool _isTranscribing;
    private long _speechCaptureGeneration;
    private long _recordingCaptureGeneration;
    private bool IsPushToTalkActive => _isRecordingAudio || _isTranscribing;
    private bool _isSpeechSynthesisEnabled;
    private bool _isTtsAvailable;
    private bool _isAsrAvailable;
    private string? _ttsSessionId;
    private long _ttsQueueGeneration;
    // Explicit clips and automatic replies share the same Engine playback queue.
    private long _avatarQueueEndTimestamp;
    private int _pendingSpeechRequests;
    private bool _voiceModeResponseCompleted;
    private bool _voiceModePlaybackFinished;
    private readonly Dictionary<string, TtsStreamState> _ttsStreams = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingAvatarSpeech> _pendingAvatarSpeechByFileUrl = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _ttsSynthesisLock = new(1, 1);
    private string? _avatarSpeechTarget;
    private readonly HashSet<string> _spokenToolCallIds = new(StringComparer.Ordinal);
    private string? _speechReceiptRunId;
    private TtsStreamState? _explicitSpeechStream;
    private bool _explicitSpeechQueued;
    private AvatarEmotionCueScheduler? _emotionCues;
    private AvatarEmotionCueScheduler EmotionCues => _emotionCues ??= new AvatarEmotionCueScheduler(AvatarBridge, AvatarRuntimeSettings);

    private sealed record PendingAvatarSpeech(string Text, long Generation, TtsStreamState StreamState, IReadOnlyList<AvatarMarkupCue> Cues, double DurationMs);

    // Voice mode state
    private IJSObjectReference? _voiceModeModule;
    internal VoiceModeState _voiceModeState = VoiceModeState.Inactive;
    private Task<bool>? _speechSynthesisToggleTask;

    private bool IsVoiceModeActive => _voiceModeState != VoiceModeState.Inactive;

    private SpeechOutputTarget CurrentSpeechOutputTargets =>
        SpeechOutputPolicy.ResolveTargets(UserScope.IsInRole("Admin"), SpeechSettings.AssistantSpeechOutputMode);

    private bool ShouldRouteSpeechToAvatar =>
        SpeechOutputPolicy.IncludesAvatar(CurrentSpeechOutputTargets);

    private bool IsSpeechSynthesisEnabled
    {
        get => _isSpeechSynthesisEnabled;
        set
        {
            if (_isSpeechSynthesisEnabled == value) { return; }

            _speechSynthesisToggleTask = SetSpeechSynthesisEnabledAsync(value);
        }
    }

    private async Task UpdateSpeechAvailabilityAsync()
    {
        _isTtsAvailable = await SpeechService.IsTtsAvailableAsync();
        _isAsrAvailable = await SpeechService.IsAsrAvailableAsync();
    }

    // TTS session management
    private async Task<bool> EnsureSpeechSynthesisStateAsync(bool enabled)
    {
        if (_isSpeechSynthesisEnabled == enabled)
        {
            if (enabled)
            {
                if (!string.IsNullOrWhiteSpace(_ttsSessionId))
                {
                    return true;
                }
            }
            else if (string.IsNullOrWhiteSpace(_ttsSessionId))
            {
                return true;
            }
        }

        var existingTask = _speechSynthesisToggleTask;
        if (existingTask is not null)
        {
            await existingTask;

            if (_isSpeechSynthesisEnabled == enabled)
            {
                return enabled
                    ? !string.IsNullOrWhiteSpace(_ttsSessionId)
                    : string.IsNullOrWhiteSpace(_ttsSessionId);
            }
        }

        return await SetSpeechSynthesisEnabledAsync(enabled);
    }

    private async Task<bool> SetSpeechSynthesisEnabledAsync(bool enabled)
    {
        _isSpeechSynthesisEnabled = enabled;
        var toggleTask = ApplySpeechSynthesisStateAsync(enabled);
        _speechSynthesisToggleTask = toggleTask;

        try
        {
            return await toggleTask;
        }
        finally
        {
            if (ReferenceEquals(_speechSynthesisToggleTask, toggleTask))
            {
                _speechSynthesisToggleTask = null;
            }
        }
    }

    private async Task<bool> ApplySpeechSynthesisStateAsync(bool enabled)
    {
        if (enabled)
        {
            try
            {
                _ttsSessionId = $"{Guid.NewGuid():N}";
                var started = await SpeechService.StartSessionAsync(_ttsSessionId, SpeechSettings.SelectedVoiceId);
                if (!started)
                {
                    ToastService.Show(Loc.GetOrDefault(ChatTextKeys.UnableToStartSpeechSynthesis, "Unable to start speech synthesis."), ToastType.Error);
                    _isSpeechSynthesisEnabled = false;
                    await InvokeAsync(StateHasChanged);
                    return false;
                }

                await InitializeAudioQueueAsync();
                return true;
            }
            catch (Exception ex)
            {
                ToastService.Show(Loc.GetOrDefault(ChatTextKeys.UnableToStartSpeechSynthesis, "Unable to start speech synthesis."), ToastType.Error);
                Logger.LogError(ex, "Failed to start speech synthesis session.");
                _isSpeechSynthesisEnabled = false;
                await InvokeAsync(StateHasChanged);
                return false;
            }
        }
        else
        {
            _ttsStreams.Clear();
            _pendingAvatarSpeechByFileUrl.Clear();
            ResetSpeechPlaybackGeneration();
            var sessionToStop = _ttsSessionId;
            _ttsSessionId = null;

            if (_audioQueueModule is not null)
            {
                await _audioQueueModule.InvokeVoidAsync("clear");
            }

            await StopAvatarSpeechIfStartedAsync();

            if (!string.IsNullOrWhiteSpace(sessionToStop))
            {
                await SpeechService.ClearCacheAsync(sessionToStop);
                await SpeechService.StopSessionAsync(sessionToStop);
            }

            return true;
        }
    }

    [JSInvokable]
    public async Task OnAudioPlaybackStarted(string fileUrl)
    {
        if (string.IsNullOrWhiteSpace(fileUrl))
        {
            return;
        }

        if (!_pendingAvatarSpeechByFileUrl.Remove(fileUrl, out var pendingSpeech))
        {
            return;
        }

        if (!IsSpeechPlaybackEnabled(pendingSpeech.StreamState) ||
            Interlocked.Read(ref _ttsQueueGeneration) != pendingSpeech.Generation ||
            (!pendingSpeech.StreamState.IsExplicit && !ShouldRouteSpeechToAvatar))
        {
            return;
        }

        try
        {
            var started = await NotifyAvatarSpeechQueueAsync(fileUrl, pendingSpeech.Text, pendingSpeech.StreamState);
            if (started && IsSpeechPlaybackEnabled(pendingSpeech.StreamState) && Interlocked.Read(ref _ttsQueueGeneration) == pendingSpeech.Generation)
            {
                EmotionCues.Schedule(pendingSpeech.Cues, _avatarEmotions, TimeSpan.Zero, pendingSpeech.DurationMs);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to queue avatar audio for browser-paced clip: {FileUrl}", fileUrl);
            if (pendingSpeech.StreamState.IsExplicit) { ShowExplicitSpeechDeliveryFailure(); }
        }
    }

    [JSInvokable]
    public async Task OnAllPlaybackFinished()
    {
        if (!IsVoiceModeActive) return;

        await InvokeAsync(MarkSpeechPlaybackFinishedAsync);
    }

    private bool QueueSpeech(string text, TtsStreamState streamState, IReadOnlyList<AvatarMarkupCue> cues)
    {
        if (string.IsNullOrWhiteSpace(text) || !_isSpeechSynthesisEnabled)
        {
            return false;
        }

        var capturedSessionId = _ttsSessionId;
        if (string.IsNullOrWhiteSpace(capturedSessionId))
        {
            return false;
        }

        var capturedGeneration = Interlocked.Read(ref _ttsQueueGeneration);
        Interlocked.Increment(ref _pendingSpeechRequests);
        _ = ProcessSpeechRequestAsync(text, capturedSessionId, capturedGeneration, streamState, cues);
        return true;
    }

    /// <summary>Discards queued response speech while preserving the user's speech setting.</summary>
    private async Task DiscardQueuedSpeechAsync()
    {
        ResetSpeechPlaybackGeneration();
        _pendingAvatarSpeechByFileUrl.Clear();
        if (_audioQueueModule is not null)
        {
            await _audioQueueModule.InvokeVoidAsync("clear");
        }

        await StopAvatarSpeechIfStartedAsync();
    }

    internal async Task<bool> QueueSpeechForMessageAsync(ChatDisplayMessage message, bool isFinal)
    {
        if (!_isSpeechSynthesisEnabled)
        {
            return false;
        }

        var messageId = message.MessageId;
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return false;
        }

        if (!_ttsStreams.TryGetValue(messageId, out var streamState))
        {
            streamState = new TtsStreamState
            {
                MarkupParser = _avatarEmotions.Count > 0 ? new AvatarMarkupParser(_avatarEmotions) : null
            };
            if (ShouldRouteSpeechToAvatar)
            {
                // One bounded connect + apply attempt per assistant reply, not per clip.
                streamState.AvatarRoutingEnabled = await AvatarRuntimeSettings.EnsureReadyAsync();
            }

            _ttsStreams[messageId] = streamState;
        }

        var queuedAny = false;
        var textSegmentIndex = 0;
        foreach (var segment in message.Segments.Where(segment => segment.Type == ChatSegmentType.Text))
        {
            if (streamState.SegmentTextLengths.Count <= textSegmentIndex)
            {
                streamState.SegmentTextLengths.Add(0);
            }

            var previousLength = streamState.SegmentTextLengths[textSegmentIndex];
            var currentText = segment.Text;
            if (currentText.Length < previousLength)
            {
                previousLength = 0;
            }

            if (currentText.Length > previousLength)
            {
                var appendedText = currentText[previousLength..];
                foreach (var token in EnumerateSpeechTokens(appendedText))
                {
                    var sentence = streamState.SegmentProvider.Push(token);
                    var (plainSpeechText, cues) = PrepareSpeechClip(sentence, streamState);
                    if (string.IsNullOrWhiteSpace(plainSpeechText))
                    {
                        continue;
                    }

                    queuedAny = QueueSpeech(plainSpeechText, streamState, cues) || queuedAny;
                }
            }

            streamState.SegmentTextLengths[textSegmentIndex] = currentText.Length;
            textSegmentIndex++;
        }

        if (!isFinal)
        {
            return queuedAny;
        }

        var trailingText = streamState.SegmentProvider.Flush();
        _ttsStreams.Remove(messageId);
        var (plainTrailingText, trailingCues) = PrepareSpeechClip(trailingText, streamState);

        if (string.IsNullOrWhiteSpace(plainTrailingText))
        {
            return queuedAny;
        }

        return QueueSpeech(plainTrailingText, streamState, trailingCues) || queuedAny;
    }

    // Markup is removed before Markdown cleanup so cue offsets match the spoken words.
    private static (string Text, IReadOnlyList<AvatarMarkupCue> Cues) PrepareSpeechClip(string clip, TtsStreamState streamState)
    {
        if (streamState.MarkupParser is null)
        {
            return (SpeechOutputPolicy.SanitizeMarkdownForSpeech(clip), []);
        }

        var markup = streamState.MarkupParser.Parse(clip);
        return (SpeechOutputPolicy.SanitizeMarkdownForSpeech(markup.Text), markup.Cues);
    }

    private async Task ProcessSpeechRequestAsync(string text, string capturedSessionId, long capturedGeneration, TtsStreamState streamState, IReadOnlyList<AvatarMarkupCue> cues)
    {
        try
        {
            await _ttsSynthesisLock.WaitAsync();

            try
            {
                // Stop invalidates requests still waiting for their turn to synthesize.
                if (_ttsSessionId != capturedSessionId || !_isSpeechSynthesisEnabled ||
                    Interlocked.Read(ref _ttsQueueGeneration) != capturedGeneration || _isDisposed)
                {
                    return;
                }

                var targets = CurrentSpeechOutputTargets;
                var request = new SpeechSynthesisRequest
                {
                    Text = text,
                    IncludeWavHeader = true,
                    VoiceId = SpeechSettings.SelectedVoiceId
                };

                var tts = await SpeechService.SynthesizeAsync(capturedSessionId, request);

                if (_ttsSessionId != capturedSessionId ||
                    !_isSpeechSynthesisEnabled ||
                    Interlocked.Read(ref _ttsQueueGeneration) != capturedGeneration ||
                    _isDisposed)
                {
                    return;
                }

                if (tts is null)
                {
                    ShowExplicitSpeechDeliveryFailure();
                    return;
                }

                await DeliverSpeechClipAsync(text, tts, capturedGeneration, streamState, cues, targets);
            }
            finally
            {
                _ttsSynthesisLock.Release();
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to queue speech synthesis.");
            ShowExplicitSpeechDeliveryFailure();
        }
        finally
        {
            Interlocked.Decrement(ref _pendingSpeechRequests);

            if (IsVoiceModeActive && CanResumeVoiceModeListening())
            {
                await InvokeAsync(async () =>
                {
                    if (CanResumeVoiceModeListening())
                    {
                        await ResumeVoiceModeListeningAsync();
                        StateHasChanged();
                    }
                });
            }
        }
    }

    // Explicit clips and streamed replies share output routing and playback coordination.
    private async Task<bool> DeliverSpeechClipAsync(string text, SpeechSynthesisResponse tts, long capturedGeneration,
        TtsStreamState streamState, IReadOnlyList<AvatarMarkupCue> cues, SpeechOutputTarget targets)
    {
        var routeToAvatar = SpeechOutputPolicy.IncludesAvatar(targets) && streamState.AvatarRoutingEnabled;
        var playInBrowser = SpeechOutputPolicy.IncludesBrowser(targets);

        if (playInBrowser)
        {
            await InvokeAsync(async () =>
            {
                if (_audioQueueModule is null) { await InitializeAudioQueueAsync(); }

                if (!string.IsNullOrWhiteSpace(tts.FileUrl))
                {
                    if (routeToAvatar)
                    {
                        RegisterPendingAvatarSpeech(tts.FileUrl, text, capturedGeneration, streamState, cues, SpeechOutputPolicy.GetClipDurationMs(tts));
                    }

                    await _audioQueueModule!.InvokeVoidAsync("enqueueUrl", tts.FileUrl);
                }
                else if (tts.Audio?.Length > 0)
                {
                    if (routeToAvatar)
                    {
                        Logger.LogWarning("Avatar speech routing skipped: synthesized clip has no FileUrl for browser pacing.");
                    }

                    await _audioQueueModule!.InvokeVoidAsync("enqueueBase64", Convert.ToBase64String(tts.Audio), "audio/wav");
                }
                else
                {
                    return;
                }

                await _audioQueueModule!.InvokeVoidAsync("resume");
                _voiceModePlaybackFinished = false;
            });

            return !string.IsNullOrWhiteSpace(tts.FileUrl) || tts.Audio?.Length > 0;
        }

        if (routeToAvatar)
        {
            if (string.IsNullOrWhiteSpace(tts.FileUrl))
            {
                Logger.LogWarning("Avatar-only speech routing requires a synthesized clip FileUrl.");
                return false;
            }

            var dispatched = await NotifyAvatarSpeechQueueAsync(tts.FileUrl, text, streamState);
            if (!dispatched)
            {
                return false;
            }

            _voiceModePlaybackFinished = false;

            var playbackDurationMs = SpeechOutputPolicy.GetClipDurationMs(tts);
            // The Engine plays clips back to back, so this clip starts when the queued ones end, or now after a synthesis gap.
            var now = Stopwatch.GetTimestamp();
            var clipStart = Math.Max(now, _avatarQueueEndTimestamp);
            if (IsSpeechPlaybackEnabled(streamState) && Interlocked.Read(ref _ttsQueueGeneration) == capturedGeneration)
            {
                EmotionCues.Schedule(cues, _avatarEmotions, Stopwatch.GetElapsedTime(now, clipStart), playbackDurationMs);
            }

            _avatarQueueEndTimestamp = clipStart + (long)(playbackDurationMs * Stopwatch.Frequency / 1000);
            _ = CompleteAvatarOnlyPlaybackAfterDelayAsync(_avatarQueueEndTimestamp, capturedGeneration, streamState);
            return true;
        }

        return false;
    }

    private void RegisterPendingAvatarSpeech(string fileUrl, string text, long generation, TtsStreamState streamState, IReadOnlyList<AvatarMarkupCue> cues, double durationMs)
    {
        if (!streamState.AvatarRoutingEnabled || string.IsNullOrWhiteSpace(fileUrl))
        {
            return;
        }

        _pendingAvatarSpeechByFileUrl[fileUrl] = new PendingAvatarSpeech(text, generation, streamState, cues, durationMs);
    }

    private async Task<bool> NotifyAvatarSpeechQueueAsync(string fileUrl, string text, TtsStreamState streamState)
    {
        if (!streamState.AvatarRoutingEnabled || string.IsNullOrWhiteSpace(fileUrl))
        {
            return false;
        }

        var audioUrl = WebHostInfo.BuildAbsoluteUrl(fileUrl);
        if (string.IsNullOrWhiteSpace(audioUrl))
        {
            Logger.LogWarning("Avatar speech routing skipped: could not build an absolute URL for {FileUrl}.", fileUrl);
            streamState.AvatarRoutingEnabled = false;
            if (streamState.IsExplicit) { ShowExplicitSpeechDeliveryFailure(); }

            return false;
        }

        var capturedGeneration = Interlocked.Read(ref _ttsQueueGeneration);
        var settings = await AvatarRuntimeSettings.GetSettingsAsync();
        var started = await AvatarBridge.NotifySpeechQueueAsync(audioUrl, text, settings.BridgeUrl);
        if (!started)
        {
            // Mid-sequence failure: stop posting avatar clips for the remainder of this reply.
            // The next reply re-checks readiness from scratch via EnsureReadyAsync.
            AvatarRuntimeSettings.MarkUnready();
            streamState.AvatarRoutingEnabled = false;
            if (streamState.IsExplicit) { ShowExplicitSpeechDeliveryFailure(); }

            return false;
        }

        // A first clip may be accepted after Stop observed that no clip had started yet.
        if (_isDisposed || Interlocked.Read(ref _ttsQueueGeneration) != capturedGeneration)
        {
            await AvatarBridge.StopSpeechAsync(settings.BridgeUrl);
            return false;
        }

        _avatarSpeechTarget = settings.BridgeUrl;
        return true;
    }

    private async Task StopAvatarSpeechIfStartedAsync()
    {
        // Every speech stop path ends the running emotion window too.
        await EmotionCues.CancelAsync();
        if (_avatarSpeechTarget is not { } target)
        {
            return;
        }

        _avatarSpeechTarget = null;
        await AvatarBridge.StopSpeechAsync(target);
    }

    private async Task CompleteAvatarOnlyPlaybackAfterDelayAsync(long queueEndTimestamp, long capturedGeneration, TtsStreamState streamState)
    {
        var remainingMs = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), queueEndTimestamp).TotalMilliseconds;
        var delay = TimeSpan.FromMilliseconds(Math.Clamp(remainingMs, 250, 600_000));
        await Task.Delay(delay);

        if (_isDisposed || !IsSpeechPlaybackEnabled(streamState) || Interlocked.Read(ref _ttsQueueGeneration) != capturedGeneration)
        {
            return;
        }

        // Only the last queued clip completes playback, regardless of which speech source supplied it.
        if (_avatarQueueEndTimestamp != queueEndTimestamp)
        {
            return;
        }

        try
        {
            await InvokeAsync(MarkSpeechPlaybackFinishedAsync);
        }
        catch (ObjectDisposedException) when (_isDisposed)
        {
        }
    }

    private bool IsSpeechPlaybackEnabled(TtsStreamState streamState) => streamState.IsExplicit || _isSpeechSynthesisEnabled;

    private void ResetSpeechPlaybackGeneration()
    {
        Interlocked.Increment(ref _ttsQueueGeneration);
        _avatarQueueEndTimestamp = 0;
    }

    private void ShowExplicitSpeechDeliveryFailure() =>
        ToastService.Show(Loc.GetOrDefault(ChatTextKeys.UnableToStartSpeechSynthesis, "Unable to start speech synthesis."), ToastType.Error);

    private async Task InitializeAudioQueueAsync()
    {
        _audioQueueModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "/js/audioQueue.js");
        await _audioQueueModule.InvokeVoidAsync("init", _audioElementId);
        await _audioQueueModule.InvokeVoidAsync("setDotNetRef", _dotNetRef);
        await _audioQueueModule.InvokeVoidAsync("activate");
    }

    // Only the initiating live panel consumes prepared speech from completed tool results.
    private async Task<bool> QueueExplicitSpeechAsync(string runId, IReadOnlyList<ChatDisplayMessage> messages)
    {
        if (_speechReceiptRunId != runId)
        {
            _speechReceiptRunId = runId;
            _spokenToolCallIds.Clear();
            _explicitSpeechStream = new TtsStreamState { IsExplicit = true };
            _explicitSpeechQueued = false;
        }

        var toolName = ToolNaming.BuildToolsetQualifiedName<SpeechToolset>(nameof(SpeechToolset.SpeakAsync));
        foreach (var segment in messages.SelectMany(message => message.Segments))
        {
            if (segment.ExecutionState != ToolExecutionState.Completed || segment.FunctionCall?.Name != toolName ||
                segment.FunctionResult is not { Exception: null } result || _spokenToolCallIds.Contains(result.CallId))
            {
                continue;
            }

            var json = result.Result switch
            {
                string value => value,
                JsonElement { ValueKind: JsonValueKind.String } value => value.GetString(),
                _ => null
            };
            if (json is null) { continue; }

            SpeechPlaybackReceipt? receipt;
            try
            {
                receipt = JsonSerializer.Deserialize<SpeechPlaybackReceipt>(json);
            }
            catch (JsonException ex)
            {
                Logger.LogError(ex, "Could not read prepared speech receipt for call {CallId}", result.CallId);
                continue;
            }

            if (receipt is null || receipt.ThreadId != ChatThread.Id || string.IsNullOrWhiteSpace(receipt.FileUrl)) { continue; }

            _spokenToolCallIds.Add(result.CallId);
            var generation = Interlocked.Read(ref _ttsQueueGeneration);
            Interlocked.Increment(ref _pendingSpeechRequests);
            await _ttsSynthesisLock.WaitAsync();
            try
            {
                var stream = _explicitSpeechStream!;
                stream.AvatarRoutingEnabled = SpeechOutputPolicy.IncludesAvatar(receipt.Targets) && await AvatarRuntimeSettings.EnsureReadyAsync();
                if (SpeechOutputPolicy.IncludesAvatar(receipt.Targets) && !stream.AvatarRoutingEnabled)
                {
                    ShowExplicitSpeechDeliveryFailure();
                }

                if (_isDisposed || generation != Interlocked.Read(ref _ttsQueueGeneration)) { continue; }

                _explicitSpeechQueued |= await DeliverSpeechClipAsync(receipt.Text,
                    new SpeechSynthesisResponse { FileUrl = receipt.FileUrl, DurationMs = receipt.DurationMs },
                    generation, stream, [], receipt.Targets);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Explicit speech delivery failed for call {CallId}", result.CallId);
                ShowExplicitSpeechDeliveryFailure();
            }
            finally
            {
                _ttsSynthesisLock.Release();
                Interlocked.Decrement(ref _pendingSpeechRequests);
            }
        }
        // The final projection can arrive while a clip dispatched earlier is still playing.
        return _explicitSpeechQueued;
    }

    private async Task MarkSpeechPlaybackFinishedAsync()
    {
        _voiceModePlaybackFinished = true;

        if (IsVoiceModeActive && CanResumeVoiceModeListening())
        {
            await ResumeVoiceModeListeningAsync();
            StateHasChanged();
        }
    }

    private static IEnumerable<string> EnumerateSpeechTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (!char.IsWhiteSpace(text[index]))
            {
                continue;
            }

            yield return text[start..(index + 1)];
            start = index + 1;
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }

    // Push-to-talk (existing ASR)
    private async Task ToggleVoiceInput()
    {
        if (_isTranscribing) return;
        if (_isRecordingAudio)
        {
            await StopVoiceInput();
            return;
        }

        if (IsInputLocked) return;
        await StartVoiceInput();
    }

    private async Task StartVoiceInput()
    {
        try
        {
            _isRecordingAudio = true;
            _recordingCaptureGeneration = _speechCaptureGeneration;
            InputPlaceholder = GetListeningPlaceholder();
            _audioRecorderModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "/js/audioRecorder.js");
            if (_isDisposed)
            {
                await _audioRecorderModule.DisposeAsync();
                return;
            }

            await _audioRecorderModule.InvokeVoidAsync("start");
        }
        catch (Exception ex)
        {
            ToastService.Show(ex.Message, ToastType.Error);
            _isRecordingAudio = false;
            InputPlaceholder = GetDefaultInputPlaceholder();
        }
    }

    private async Task StopVoiceInput()
    {
        var captureGeneration = _recordingCaptureGeneration;
        try
        {
            _isRecordingAudio = false;
            _isTranscribing = true;
            InputPlaceholder = GetTranscribingPlaceholder();
            _audioRecorderModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "/js/audioRecorder.js");
            await using var jsStream = await _audioRecorderModule.InvokeAsync<IJSStreamReference>("stop");
            if (jsStream is not null)
            {
                await using var stream = await jsStream.OpenReadStreamAsync(20 * ByteSize.OneMegabyte);
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                var base64 = Convert.ToBase64String(ms.ToArray());
                if (IsInputLocked || _isDisposed || captureGeneration != _speechCaptureGeneration)
                {
                    return;
                }

                var transcription = await SpeechService.TranscribeAsync(base64, "auto");
                if (IsInputLocked || _isDisposed || captureGeneration != _speechCaptureGeneration)
                {
                    return;
                }

                if (transcription is null)
                {
                    ToastService.Show(Loc.GetOrDefault(ChatTextKeys.UnableToTranscribeAudio, "Unable to transcribe audio."), ToastType.Error);
                    return;
                }

                _inputText = transcription.Text ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            ToastService.Show(ex.Message, ToastType.Error);
        }
        finally
        {
            _isTranscribing = false;
            InputPlaceholder = GetDefaultInputPlaceholder();
        }
    }

    // Voice mode — continuous conversation
    private async Task ToggleVoiceModeAsync()
    {
        if (IsVoiceModeActive)
        {
            await StopVoiceModeAsync();
        }
        else
        {
            await StartVoiceModeAsync();
        }
    }

    private async Task StartVoiceModeAsync()
    {
        try
        {
            // Voice mode requires both ASR and TTS
            if (!_isAsrAvailable || !_isTtsAvailable)
            {
                ToastService.Show(Loc.GetOrDefault(ChatTextKeys.VoiceModeMissingServices, "Voice mode requires both speech recognition and synthesis services."), ToastType.Error);
                return;
            }

            // Enable TTS if not already on
            if (!_isSpeechSynthesisEnabled)
            {
                var ttsReady = await EnsureSpeechSynthesisStateAsync(true);
                if (!ttsReady)
                {
                    return;
                }
            }

            _voiceModeModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "/js/voiceMode.js");
            await _voiceModeModule.InvokeVoidAsync("start", _dotNetRef);
            ResetVoiceModeTurnState();
            await ResumeVoiceModeListeningAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to start voice mode.");
            ToastService.Show(Loc.GetOrDefault(ChatTextKeys.VoiceModeStartFailed, "Failed to start voice mode."), ToastType.Error);
            _voiceModeState = VoiceModeState.Inactive;
            InputPlaceholder = GetDefaultInputPlaceholder();
        }
    }

    private async Task StopVoiceModeAsync()
    {
        _speechCaptureGeneration++;
        try
        {
            ResetVoiceModeTurnState();
            _voiceModeState = VoiceModeState.Inactive;
            InputPlaceholder = GetDefaultInputPlaceholder();
            ResetSpeechPlaybackGeneration();
            _pendingAvatarSpeechByFileUrl.Clear();

            if (_voiceModeModule is not null)
            {
                await _voiceModeModule.InvokeVoidAsync("stop");
            }

            // Clear any playing TTS
            if (_audioQueueModule is not null)
            {
                await _audioQueueModule.InvokeVoidAsync("clear");
            }

            await StopAvatarSpeechIfStartedAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to stop voice mode.");
        }
    }

    private async Task PauseVadAsync()
    {
        if (_voiceModeModule is not null)
        {
            await _voiceModeModule.InvokeVoidAsync("pause");
        }
    }

    private async Task ResumeVadAsync()
    {
        if (_voiceModeModule is not null)
        {
            await _voiceModeModule.InvokeVoidAsync("resume");
        }
    }

    // Called from JS when VAD detects the user started speaking
    [JSInvokable]
    public async Task OnVadSpeechStart()
    {
        await InvokeAsync(() =>
        {
            if (!IsVoiceModeActive) return;
            InputPlaceholder = GetListeningPlaceholder();
            StateHasChanged();
        });
    }

    // Called from JS when VAD captures a complete utterance
    [JSInvokable]
    public async Task OnVadSpeechEnd(string base64Audio)
    {
        await InvokeAsync(async () =>
        {
            // Only process if we are actively listening (prevents duplicate callbacks from race conditions)
            if (_voiceModeState != VoiceModeState.Listening) return;
            var captureGeneration = _speechCaptureGeneration;

            _voiceModeState = VoiceModeState.Transcribing;
            InputPlaceholder = GetTranscribingPlaceholder();
            StateHasChanged();
            await PauseVadAsync();

            try
            {
                var transcription = await SpeechService.TranscribeAsync(base64Audio, "auto");
                // Recognition belongs to the chat and capture session that recorded it.
                if (!IsVoiceModeActive || _isDisposed || captureGeneration != _speechCaptureGeneration) { return; }

                if (transcription is null || string.IsNullOrWhiteSpace(transcription.Text))
                {
                    await ResumeVoiceModeListeningAsync();
                    StateHasChanged();
                    return;
                }

                ResetVoiceModeTurnState();
                _voiceModeState = VoiceModeState.Processing;
                InputPlaceholder = Loc.GetOrDefault(ChatTextKeys.ProcessingPlaceholder, "Processing...");
                _inputText = transcription.Text;
                StateHasChanged();
                await ProcessInput();

                // If ProcessInput failed (run state reverted to Idle without streaming), resume listening
                if (IsVoiceModeActive && _runState == ChatRunState.Idle && _voiceModeState == VoiceModeState.Processing)
                {
                    await ResumeVoiceModeListeningAsync();
                    StateHasChanged();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Voice mode transcription failed.");
                if (IsVoiceModeActive && !_isDisposed && captureGeneration == _speechCaptureGeneration)
                {
                    await ResumeVoiceModeListeningAsync();
                    StateHasChanged();
                }
            }
        });
    }

    // Called when the agent response is final during voice mode.
    // If TTS was queued, transitions to Speaking. Otherwise, resumes listening.
    internal async Task OnVoiceModeResponseCompletedAsync()
    {
        if (!IsVoiceModeActive) return;

        _voiceModeResponseCompleted = true;

        // Completion depends on the whole turn, including clips from earlier projections.
        if (CanResumeVoiceModeListening())
        {
            await ResumeVoiceModeListeningAsync();
        }
        else
        {
            _voiceModeState = VoiceModeState.Speaking;
            InputPlaceholder = Loc.GetOrDefault(ChatTextKeys.SpeakingPlaceholder, "Speaking...");
        }

        StateHasChanged();
    }

    private void ResetVoiceModeTurnState()
    {
        _voiceModeResponseCompleted = false;
        // No playback is outstanding until a clip is actually delivered.
        _voiceModePlaybackFinished = true;
    }

    private async Task ResumeVoiceModeListeningAsync()
    {
        ResetVoiceModeTurnState();
        _voiceModeState = VoiceModeState.Listening;
        InputPlaceholder = Loc.GetOrDefault(ChatTextKeys.VoiceModeWaitingPlaceholder, "Waiting for speech...");
        await ResumeVadAsync();
    }

    private string GetDefaultInputPlaceholder()
    {
        return Loc.GetOrDefault(ChatTextKeys.TypeYourMessagePlaceholder, "Type your message...");
    }

    private string GetListeningPlaceholder()
    {
        return Loc.GetOrDefault(ChatTextKeys.VoiceModeListeningPlaceholder, "Listening...");
    }

    private string GetTranscribingPlaceholder()
    {
        return Loc.GetOrDefault(ChatTextKeys.TranscribingPlaceholder, "Transcribing...");
    }

    private bool CanResumeVoiceModeListening()
    {
        return _voiceModeResponseCompleted &&
               _voiceModePlaybackFinished &&
               Volatile.Read(ref _pendingSpeechRequests) == 0;
    }
}
