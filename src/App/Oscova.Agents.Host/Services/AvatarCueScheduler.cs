using Oscova.Agents.Core.Avatar;

namespace Oscova.Agents.Host.Services;

/// <summary>Plays emotion and gesture cues on the existing speech clip timeline.</summary>
public sealed class AvatarCueScheduler(IAvatarBridgeClient avatarBridge, IAvatarRuntimeSettingsService avatarSettings)
{
    private readonly Lock _gate = new();
    private CancellationTokenSource _replyCancellation = new();
    private CancellationTokenSource? _windowCancellation;
    private IReadOnlyList<string> _emotions = [];
    private string? _activeEmotion;
    private Task? _gestureRequest;

    /// <summary>Schedules one clip's cues relative to the clip's playback start.</summary>
    public void Schedule(IReadOnlyList<AvatarMarkupCue> cues, IReadOnlyList<string> emotions, TimeSpan clipStartDelay, double clipDurationMs)
    {
        CancellationToken replyToken;
        lock (_gate)
        {
            _emotions = emotions;
            replyToken = _replyCancellation.Token;
        }

        foreach (var cue in cues)
        {
            _ = FireCueAsync(cue, clipStartDelay + TimeSpan.FromMilliseconds(clipDurationMs * cue.Fraction), replyToken);
        }
    }

    /// <summary>Cancels pending cues and windows, then restores the base when a cue is showing.</summary>
    public async Task CancelAsync()
    {
        bool wasShowing;
        Task? gestureRequest;
        lock (_gate)
        {
            _replyCancellation.Cancel();
            _windowCancellation?.Dispose();
            _windowCancellation = null;
            _replyCancellation.Dispose();
            _replyCancellation = new CancellationTokenSource();
            wasShowing = _activeEmotion is not null;
            _activeEmotion = null;
            gestureRequest = _gestureRequest is null ? null : StopGestureAfterAsync(_gestureRequest);
            _gestureRequest = gestureRequest;
        }

        if (gestureRequest is not null)
        {
            await gestureRequest.ConfigureAwait(false);
        }

        if (wasShowing)
        {
            await RestoreBaseAsync().ConfigureAwait(false);
        }
    }

    private async Task FireCueAsync(AvatarMarkupCue cue, TimeSpan delay, CancellationToken replyToken)
    {
        try
        {
            await Task.Delay(delay, replyToken).ConfigureAwait(false);
            replyToken.ThrowIfCancellationRequested();
            if (cue.Kind == AvatarCueKind.Gesture)
            {
                Task request;
                lock (_gate)
                {
                    replyToken.ThrowIfCancellationRequested();
                    request = SelectGestureAfterAsync(_gestureRequest, cue.Name, replyToken);
                    _gestureRequest = request;
                }

                await request.ConfigureAwait(false);
                return;
            }

            var emotion = cue.Name;
            var settings = await avatarSettings.GetSettingsAsync().ConfigureAwait(false);

            CancellationToken windowToken;
            bool alreadyShowing;
            lock (_gate)
            {
                replyToken.ThrowIfCancellationRequested();
                // A new cue replaces the running window, so only the latest one restores.
                _windowCancellation?.Cancel();
                _windowCancellation?.Dispose();
                _windowCancellation = CancellationTokenSource.CreateLinkedTokenSource(replyToken);
                windowToken = _windowCancellation.Token;
                alreadyShowing = string.Equals(_activeEmotion, emotion, StringComparison.Ordinal);
                _activeEmotion = emotion;
            }

            // The window counts from the cue start, not from the bridge call's completion.
            var window = Task.Delay(TimeSpan.FromSeconds(settings.EmotionCueRestoreAfterSeconds), windowToken);
            if (!alreadyShowing)
            {
                await avatarBridge.SetEmotionAsync(emotion, settings.EmotionCueBlendInSeconds).ConfigureAwait(false);
            }

            await window.ConfigureAwait(false);

            lock (_gate)
            {
                if (windowToken.IsCancellationRequested)
                {
                    return;
                }

                _activeEmotion = null;
            }

            await RestoreBaseAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SelectGestureAfterAsync(Task? previous, string name, CancellationToken token)
    {
        if (previous is not null) { await previous.ConfigureAwait(false); }

        token.ThrowIfCancellationRequested();
        await avatarBridge.SelectGestureAsync(name).ConfigureAwait(false);
    }

    private async Task StopGestureAfterAsync(Task previous)
    {
        // Keep the stop in the request chain so a new reply cannot overtake it.
        try { await previous.ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        await avatarBridge.SelectGestureAsync("None").ConfigureAwait(false);
    }

    private async Task RestoreBaseAsync()
    {
        var settings = await avatarSettings.GetSettingsAsync().ConfigureAwait(false);
        var emotions = _emotions;
        // The current base, else the Engine's first emotion, which is neutral.
        var baseEmotion = emotions.FirstOrDefault(emotion => string.Equals(emotion, settings.SelectedEmotion, StringComparison.OrdinalIgnoreCase))
            ?? emotions[0];
        await avatarBridge.SetEmotionAsync(baseEmotion, settings.EmotionBlendSpeed).ConfigureAwait(false);
    }
}
