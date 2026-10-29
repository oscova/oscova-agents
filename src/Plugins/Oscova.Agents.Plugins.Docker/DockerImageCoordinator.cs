using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
namespace Oscova.Agents.Plugins.Docker;

/// <summary>Retains one shared manual image build independently of settings viewers.</summary>
public sealed class DockerImageCoordinator(SandboxImageBuilder builder, DockerImageSettings settings, ILogger logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _admission = new(1, 1);
    private readonly Lock _state = new();
    private CancellationTokenSource? _cancel;
    private Task? _build;
    private bool _building;
    [SuppressMessage("Design", "MA0046", Justification = "The typed viewer contract carries one immutable output update.")]
    public event Action<SandboxBuildOutputEvent>? Output;
    public bool IsBuilding { get { lock (_state) { return _building; } } }
    /// <summary>Returns the shared image status after validating viewer access.</summary>
    public async Task<DockerImageState> GetAsync(PluginSettingsContext context, CancellationToken ct)
    {
        await context.RequireAccessAsync(ct).ConfigureAwait(false);
        await settings.LoadAsync(ct).ConfigureAwait(false);
        bool built;
        try { built = !IsBuilding && await SandboxImageBuilder.ImageBuiltAsync(ct).ConfigureAwait(false); }
        catch (InvalidOperationException ex) { logger.LogWarning(ex, "Docker image status is unavailable."); built = false; }
        return new(settings.CustomDockerfile, SandboxImageBuilder.ReadDefaultDockerfile(), built, IsBuilding,
            await context.IsOfflineAsync(ct).ConfigureAwait(false));
    }
    /// <summary>Saves image instructions only while no shared build is active.</summary>
    public async Task SaveAsync(PluginSettingsContext context, string content, CancellationToken ct)
    {
        await context.RequireAccessAsync(ct).ConfigureAwait(false);
        await _admission.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsBuilding) { throw new InvalidOperationException("A sandbox image build is already running."); }
            await settings.SaveAsync(context.Settings, content, ct).ConfigureAwait(false);
        }
        finally { _admission.Release(); }
    }
    /// <summary>Starts one installation build after validating viewer authority and offline policy.</summary>
    public async Task StartAsync(PluginSettingsContext context, bool noCache, bool reset, CancellationToken ct)
    {
        await context.RequireAccessAsync(ct).ConfigureAwait(false);
        if (await context.IsOfflineAsync(ct).ConfigureAwait(false))
        { throw new InvalidOperationException("Offline mode is on: turn it off before building or resetting the sandbox image."); }
        await _admission.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsBuilding) { throw new InvalidOperationException("A sandbox image build is already running."); }
            if (reset) { await settings.SaveAsync(context.Settings, string.Empty, ct).ConfigureAwait(false); }
            else { await settings.LoadAsync(ct).ConfigureAwait(false); }
            DockerImageSettings.Validate(settings.CustomDockerfile);
            lock (_state)
            {
                _cancel = new();
                var cancellation = _cancel;
                IDisposable? registration;
                try { registration = context.RegisterOperation?.Invoke(cancellation.Cancel); }
                catch { cancellation.Dispose(); _cancel = null; throw; }
                _building = true;
                _build = Task.Run(() => RunAsync(noCache, cancellation, registration));
            }
        }
        finally { _admission.Release(); }
    }
    /// <summary>Cancels the shared build after validating the requesting viewer.</summary>
    public async Task CancelAsync(PluginSettingsContext context, CancellationToken ct)
    {
        await context.RequireAccessAsync(ct).ConfigureAwait(false);
        lock (_state) { _cancel?.Cancel(); }
    }
    private async Task RunAsync(bool noCache, CancellationTokenSource cancellation, IDisposable? registration)
    {
        SandboxBuildOutputEvent terminal;
        try
        {
            var succeeded = await builder.RebuildAsync(noCache, (line, error) => Broadcast(new(line, error, false, false)), cancellation.Token).ConfigureAwait(false);
            terminal = new(string.Empty, !succeeded, true, succeeded);
        }
        catch (OperationCanceledException) { terminal = new("Build cancelled.", true, true, false); }
        catch (Exception ex) { logger.LogError(ex, "Docker image build failed."); terminal = new(ex.Message, true, true, false); }
        finally
        {
            lock (_state) { _building = false; _cancel = null; }
            cancellation.Dispose();
            registration?.Dispose();
        }
        Broadcast(terminal);
    }
    private void Broadcast(SandboxBuildOutputEvent value)
    {
        foreach (var handler in Output?.GetInvocationList() ?? [])
        {
            try { ((Action<SandboxBuildOutputEvent>)handler)(value); }
            catch (Exception ex) { logger.LogWarning(ex, "Docker image viewer could not receive an update."); }
        }
    }
    /// <summary>Stops and awaits the retained build before provider configuration changes.</summary>
    public async Task StopAsync()
    {
        Task? build;
        lock (_state) { _cancel?.Cancel(); build = _build; }
        if (build is not null) { await build.ConfigureAwait(false); }
    }
    /// <inheritdoc />
    public async ValueTask DisposeAsync() { await StopAsync().ConfigureAwait(false); _admission.Dispose(); }
}
/// <summary>Reports the current shared image and the authenticated viewer's offline policy.</summary>
public sealed record DockerImageState(string CustomDockerfile, string DefaultDockerfile, bool ImageBuilt, bool IsBuilding, bool Offline);
/// <summary>Supplies one typed image operation without private HTTP or hub transport.</summary>
public sealed record SandboxBuildOutputEvent(string Line, bool IsError, bool Completed, bool Succeeded);
/// <summary>Retains the visible output style supplied by the original image panel.</summary>
public sealed record SandboxBuildOutputLine(string Text, bool IsError);
/// <summary>Supplies typed image operations for a viewer bound to authenticated Host policy.</summary>
public interface IDockerImagePanel
{
    [SuppressMessage("Design", "MA0046", Justification = "The typed viewer contract carries one immutable output update.")]
    event Action<SandboxBuildOutputEvent>? Output;
    /// <summary>Returns the shared image state for this authenticated viewer.</summary>
    Task<DockerImageState> GetAsync();
    /// <summary>Saves validated image instructions without changing a running build.</summary>
    Task SaveAsync(string content);
    /// <summary>Starts one shared build, optionally clearing its cache or saved instructions.</summary>
    Task StartAsync(bool noCache, bool reset);
    /// <summary>Cancels the current shared build after validating viewer authority.</summary>
    Task CancelAsync();
}
/// <summary>Pairs authenticated interaction adapters with the shared image operations.</summary>
public sealed record DockerPanelContext(IDockerImagePanel Images, IPluginSettingsUi Ui, string? EditorHelpTopic = null);
/// <summary>Adapts one viewer to the shared coordinator without owning its build lifetime.</summary>
public sealed class DockerImagePanel(DockerImageCoordinator images, PluginSettingsContext context) : IDockerImagePanel
{
    public event Action<SandboxBuildOutputEvent>? Output { add => images.Output += value; remove => images.Output -= value; }
    /// <inheritdoc />
    public Task<DockerImageState> GetAsync() => images.GetAsync(context, CancellationToken.None);
    /// <inheritdoc />
    public Task SaveAsync(string content) => images.SaveAsync(context, content, CancellationToken.None);
    /// <inheritdoc />
    public Task StartAsync(bool noCache, bool reset) => images.StartAsync(context, noCache, reset, CancellationToken.None);
    /// <inheritdoc />
    public Task CancelAsync() => images.CancelAsync(context, CancellationToken.None);
}
