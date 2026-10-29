using Microsoft.JSInterop;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Infrastructure.Users;

namespace Oscova.Agents.Host.Services.UI;

/// <summary>
/// Plays short, optional UI sound cues through a small JS module, gated by the user's
/// <see cref="SoundSettings"/>. Scoped to the Blazor circuit so it reuses one imported
/// module and the current user's preferences.
/// </summary>
public sealed class SoundCuePlayer : IAsyncDisposable
{
    private const string ModulePath = "/js/soundCues.js";
    private const string SoundsBasePath = "/sounds";

    private static readonly string[] CueIds = Enum.GetValues<SoundCue>().Select(CueId).ToArray();

    private readonly IJSRuntime _js;
    private readonly UserServiceScope _userScope;
    private readonly ILogger<SoundCuePlayer> _logger;
    private Task<IJSObjectReference>? _moduleTask;

    public SoundCuePlayer(IJSRuntime js, UserServiceScope userScope, ILogger<SoundCuePlayer> logger)
    {
        _js = js;
        _userScope = userScope;
        _logger = logger;
    }

    /// <summary>Plays the given cue, honoring the user's master + per-cue sound settings.</summary>
    public async Task PlayAsync(SoundCue cue)
    {
        if (!_userScope.SoundSettings.MasterEnabled)
        {
            return;
        }

        try
        {
            var module = await EnsureModuleAsync().ConfigureAwait(false);
            await module.InvokeVoidAsync("play", CueId(cue)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ClearFailedModuleTask();
            // Interop fails during prerender/circuit teardown; a cue must never disrupt the action that triggered it.
            _logger.LogDebug(ex, "Failed to play sound cue {Cue}.", cue);
        }
    }

    /// <summary>Re-applies the current sound settings to the loaded module after a settings change.</summary>
    public async Task ReconfigureAsync()
    {
        if (_moduleTask is null)
        {
            return;
        }

        try
        {
            var module = await _moduleTask.ConfigureAwait(false);
            await ConfigureAsync(module).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ClearFailedModuleTask();
            _logger.LogDebug(ex, "Failed to reconfigure sound cues.");
        }
    }

    private Task<IJSObjectReference> EnsureModuleAsync() => _moduleTask ??= LoadModuleAsync();

    private async Task<IJSObjectReference> LoadModuleAsync()
    {
        var module = await _js.InvokeAsync<IJSObjectReference>("import", ModulePath).ConfigureAwait(false);
        await module.InvokeVoidAsync("preload", CueIds, SoundsBasePath).ConfigureAwait(false);
        await ConfigureAsync(module).ConfigureAwait(false);
        return module;
    }

    private Task ConfigureAsync(IJSObjectReference module)
    {
        var settings = _userScope.SoundSettings;
        var cues = CueIds.ToDictionary(id => id, settings.IsCueEnabled);
        return module.InvokeVoidAsync(
            "configure",
            new { enabled = settings.MasterEnabled, volume = settings.Volume, cues }).AsTask();
    }

    private static string CueId(SoundCue cue) => cue.ToString().ToLowerInvariant();

    private void ClearFailedModuleTask()
    {
        if (_moduleTask is not null && (_moduleTask.IsFaulted || _moduleTask.IsCanceled))
        {
            _moduleTask = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_moduleTask is null)
        {
            return;
        }

        try
        {
            var module = await _moduleTask.ConfigureAwait(false);
            await module.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Circuit teardown races are expected; the module ref is the only thing to release.
        }
    }
}
