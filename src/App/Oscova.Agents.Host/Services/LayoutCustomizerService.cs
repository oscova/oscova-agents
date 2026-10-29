using System.Text.Json.Serialization;
using Microsoft.JSInterop;

namespace Oscova.Agents.Host.Services;

public class LayoutCustomizerService : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private IJSObjectReference? _module;

    public LayoutCustomizerService(IJSRuntime js)
    {
        _js = js;
    }

    private static readonly (string Key, string Label)[] ThemeOptions =
    [
        ("light", "Light"),
        ("dark", "Dark"),
        ("system", "System")
    ];

    private static readonly (string Key, string Label)[] FontFamilies =
    [
        ("dm-sans", "DM Sans"),
        ("wix", "Wix"),
        ("inclusive", "Inclusive"),
        ("ar-one", "AR One")
    ];

    public IReadOnlyList<(string Key, string Label)> AvailableThemes => ThemeOptions;

    public IReadOnlyList<(string Key, string Label)> AvailableFonts => FontFamilies;

    private async Task<IJSObjectReference> GetModuleAsync()
    {
        _module ??= await _js.InvokeAsync<IJSObjectReference>("import", "/js/layoutCustomizer.js").ConfigureAwait(false);
        return _module;
    }

    public async Task<LayoutConfig> GetConfigAsync()
    {
        var module = await GetModuleAsync().ConfigureAwait(false);
        return await module.InvokeAsync<LayoutConfig>("getConfig").ConfigureAwait(false);
    }

    public async Task InitializeAsync()
    {
        var module = await GetModuleAsync().ConfigureAwait(false);
        var config = await module.InvokeAsync<LayoutConfig>("getConfig").ConfigureAwait(false);
        await module.InvokeVoidAsync("applyConfig", config).ConfigureAwait(false);
    }

    public async Task SetThemeAsync(string theme)
    {
        var module = await GetModuleAsync().ConfigureAwait(false);
        await module.InvokeVoidAsync("setTheme", theme).ConfigureAwait(false);
    }

    /// <summary>Switches to the opposite of the browser's current theme appearance.</summary>
    public async Task ToggleThemeAsync()
    {
        var module = await GetModuleAsync().ConfigureAwait(false);
        await module.InvokeVoidAsync("toggleTheme").ConfigureAwait(false);
    }

    public async Task SetFontFamilyAsync(string fontFamily)
    {
        var module = await GetModuleAsync().ConfigureAwait(false);
        await module.InvokeVoidAsync("setFontFamily", fontFamily).ConfigureAwait(false);
    }

    public async Task SetSidebarThemeAsync(string sidebarTheme)
    {
        var module = await GetModuleAsync().ConfigureAwait(false);
        await module.InvokeVoidAsync("setSidebarTheme", sidebarTheme).ConfigureAwait(false);
    }

    public async Task SetDirectionAsync(string direction)
    {
        var module = await GetModuleAsync().ConfigureAwait(false);
        await module.InvokeVoidAsync("setDirection", direction).ConfigureAwait(false);
    }

    /// <summary>Sets whether the authenticated shell uses its wallpaper and glass effects.</summary>
    public async Task SetWallpaperEffectsEnabledAsync(bool enabled)
    {
        var module = await GetModuleAsync().ConfigureAwait(false);
        await module.InvokeVoidAsync("setWallpaperEffectsEnabled", enabled).ConfigureAwait(false);
    }

    public async Task ToggleFullscreenAsync()
    {
        var module = await GetModuleAsync().ConfigureAwait(false);
        await module.InvokeVoidAsync("toggleFullscreen").ConfigureAwait(false);
    }

    public async Task<LayoutConfig> ResetAsync()
    {
        var module = await GetModuleAsync().ConfigureAwait(false);
        return await module.InvokeAsync<LayoutConfig>("resetConfig").ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync().ConfigureAwait(false);
            }
            catch (JSDisconnectedException)
            {
                // Circuit already disconnected, module will be GC'd
            }
        }
    }
}

public class LayoutConfig
{
    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "system";

    [JsonPropertyName("fontFamily")]
    public string FontFamily { get; set; } = "default";

    [JsonPropertyName("sidebarTheme")]
    public string SidebarTheme { get; set; } = "light";

    [JsonPropertyName("direction")]
    public string Direction { get; set; } = "ltr";

    [JsonPropertyName("fullscreen")]
    public bool Fullscreen { get; set; }

    [JsonPropertyName("wallpaperEffectsEnabled")]
    public bool WallpaperEffectsEnabled { get; set; } = true;
}
