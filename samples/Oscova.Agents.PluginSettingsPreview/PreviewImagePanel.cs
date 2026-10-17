using Oscova.Agents.Plugins;
using Oscova.Agents.Plugins.Docker;

namespace Oscova.Agents.PluginSettingsPreview;

/// <summary>Supplies appearance states without executing Docker or writing product settings.</summary>
public sealed class PreviewImagePanel : IDockerImagePanel, IPluginSettingsUi
{
    private DockerImageState _state = new(string.Empty, "FROM node:22-bookworm-slim", false, false, false);
    public event Action<SandboxBuildOutputEvent>? Output;
    public string Notification { get; private set; } = string.Empty;
    /// <summary>Selects the appearance state shown by the preview controls.</summary>
    public void SetState(string value)
    {
        _state = new(value == "success" ? "FROM node:22-bookworm-slim" : string.Empty,
            "FROM node:22-bookworm-slim", value == "success", value == "busy", value == "unavailable");
    }
    /// <inheritdoc />
    public Task<DockerImageState> GetAsync() => Task.FromResult(_state);
    /// <inheritdoc />
    public Task SaveAsync(string content) { _state = _state with { CustomDockerfile = content }; return Task.CompletedTask; }
    /// <inheritdoc />
    public Task StartAsync(bool noCache, bool reset)
    {
        _state = _state with { IsBuilding = true, CustomDockerfile = reset ? string.Empty : _state.CustomDockerfile };
        Output?.Invoke(new("Preview build output", false, false, false));
        return Task.CompletedTask;
    }
    /// <inheritdoc />
    public Task CancelAsync() { _state = _state with { IsBuilding = false }; Output?.Invoke(new("Build cancelled.", true, true, false)); return Task.CompletedTask; }
    /// <summary>Shows one failed-build output state without starting a process.</summary>
    public void ShowError() => Output?.Invoke(new("Preview build failed.", true, true, false));
    /// <inheritdoc />
    public void Notify(string message, PluginNotificationKind kind) => Notification = message;
    /// <inheritdoc />
    public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);
    /// <inheritdoc />
    public Task<bool> EditTextAsync(string title, Func<Task<string>> load, Func<string, Task> save, string? helpTopic = null) => Task.FromResult(false);
}
