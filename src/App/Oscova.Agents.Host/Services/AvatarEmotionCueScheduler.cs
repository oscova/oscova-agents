using Oscova.Agents.Core.Avatar;

namespace Oscova.Agents.Host.Services;

/// <summary>Plays reply emotion cues against speech playback and returns the avatar to its base emotion.</summary>
public sealed class AvatarEmotionCueScheduler(IAvatarBridgeClient avatarBridge, IAvatarRuntimeSettingsService avatarSettings)
{
    private readonly Lock _gate = new();
    private CancellationTokenSource _replyCancellation = new();
    private CancellationTokenSource? _windowCancellation;
    private IReadOnlyList<string> _emotions = [];
    private string? _activeEmotion;

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
            _ = FireCueAsync(cue.Emotion, clipStartDelay + TimeSpan.FromMilliseconds(clipDurationMs * cue.Fraction), replyToken);
        }
    }

    /// <summary>Cancels pending cues and windows, then restores the base when a cue is showing.</summary>
    public async Task CancelAsync()
    {
        bool wasShowing;
        lock (_gate)
        {
            _replyCancellation.Cancel();
            _windowCancellation?.Dispose();
            _windowCancellation = null;
            _replyCancellation.Dispose();
            _replyCancellation = new CancellationTokenSource();
            wasShowing = _activeEmotion is not null;
            _activeEmotion = null;
        }

        if (wasShowing)
        {
            await RestoreBaseAsync().ConfigureAwait(false);
        }
    }

    private async Task FireCueAsync(string emotion, TimeSpan delay, CancellationToken replyToken)
    {
        try
        {
            await Task.Delay(delay, replyToken).ConfigureAwait(false);
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
