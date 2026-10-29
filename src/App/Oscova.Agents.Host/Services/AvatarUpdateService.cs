using Oscova.Agents.Core.ApiContracts.Updates;
using Oscova.Agents.Core.Updates;
using Oscova.Agents.Infrastructure.Backup;

namespace Oscova.Agents.Host.Services;

/// <summary>Supplies optional launcher-owned Avatar operations to the standalone Host.</summary>
public sealed record AvatarEngineHostOptions(IAvatarEngineOperations? Operations);

/// <summary>Owns Avatar publication caching and a confirmed operation independently of browser lifetime.</summary>
public sealed class AvatarUpdateService(AvatarEngineHostOptions options, EnvironmentBackupService backup,
    TimeProvider clock, IHostApplicationLifetime lifetime, ILogger<AvatarUpdateService> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly Lock _operationGate = new();
    private AvatarReleaseIdentity? _published;
    private DateTimeOffset _checkedAt;
    private string? _checkError;
    private AvatarUpdateStatus? _operation;
    private Task? _operationTask;
    public event EventHandler? InstallationChanged;

    /// <summary>Reads local state and cached publication without relying on bridge reachability.</summary>
    public async Task<AvatarUpdateStatus> GetStatusAsync(bool refresh, CancellationToken ct)
    {
        if (options.Operations is not { } operations)
        {
            return new(false, OperatingSystem.IsWindows(), false, false, null, null, "Unsupported", Environment.MachineName);
        }

        var local = await operations.GetStatusAsync(ct).ConfigureAwait(false);
        await _checkGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (refresh || _checkedAt == default || clock.GetUtcNow() - _checkedAt >= TimeSpan.FromHours(6))
            {
                try
                {
                    _published = await operations.GetPublishedAsync(ct).ConfigureAwait(false);
                    _checkError = null;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _published = null;
                    _checkError = ex.Message;
                    logger.LogWarning(ex, "Avatar publication check is unavailable.");
                }

                _checkedAt = clock.GetUtcNow();
            }

            AvatarUpdateStatus? operation;
            lock (_operationGate) { operation = _operation; }

            if (operation is { IsBusy: false } && _published is not null && operation.AvailableRelease?.MatchesContent(_published) != true)
            {
                operation = null;
            }

            var state = operation?.State ?? (_published is null ? "Unavailable" :
                local.Installed && local.Release?.MatchesContent(_published) == true ? "Current" : "Available");
            if (operation is { State: "Failed", Running: true } && !local.Running &&
                local.Release?.MatchesContent(operation.AvailableRelease!) == true)
            {
                state = "InstalledNotRunning";
            }

            return new(true, local.Supported, local.Installed, local.Running, local.Release, _published, state,
                Environment.MachineName, operation?.Error ?? _checkError, operation?.ReceivedBytes ?? 0, operation?.TotalBytes ?? 0);
        }
        finally { _checkGate.Release(); }
    }

    /// <summary>Accepts reviewed content, reserving shared mutation admission before starting background work.</summary>
    public async Task<AvatarUpdateStatus> StartAsync(StartAvatarUpdateRequest request, CancellationToken ct)
    {
        if (!request.ConfirmInstallAndStop || request.Release is not { Source: "published" } || !request.Release.IsValid())
        {
            throw new ApplicationUpdateException("Review the current Avatar release and confirm installation and any process stop.");
        }

        var operations = options.Operations ?? throw new ApplicationUpdateException("Launch with 'oscova start' to manage the local Avatar Engine.");
        if (!await backup.TryAcquireAdmissionAsync(ct).ConfigureAwait(false))
        {
            throw new ApplicationUpdateConflictException("Another restore, Avatar operation or harness update is already pending.");
        }

        try
        {
            if (EnvironmentRestoreBootstrapper.HasPendingOperation() || EnvironmentRestoreBootstrapper.HasRecoveryEvidence())
            {
                throw new ApplicationUpdateConflictException("Another restore or harness update is already pending.");
            }

            var status = await GetStatusAsync(true, ct).ConfigureAwait(false);
            if (!status.Supported) { throw new ApplicationUpdateException("Avatar Engine installation is available only on Windows."); }

            if (status.AvailableRelease?.MatchesContent(request.Release) != true)
            {
                throw new ApplicationUpdateConflictException("The published Avatar release changed or is unavailable. Refresh and confirm the current release.");
            }

            if (status.Installed && status.InstalledRelease?.MatchesContent(status.AvailableRelease) == true)
            {
                throw new ApplicationUpdateConflictException("The installed Avatar Engine already matches the published content.");
            }

            lock (_operationGate)
            {
                _operation = status with { State = "Downloading", Error = null, ReceivedBytes = 0, TotalBytes = request.Release.SizeBytes };
                // The operation retains server shutdown cancellation, not the accepting browser request.
                _operationTask = Task.Run(() => RunAsync(operations, request.Release), CancellationToken.None);
                return _operation;
            }
        }
        catch { backup.ReleaseAdmission(); throw; }
    }

    private async Task RunAsync(IAvatarEngineOperations operations, AvatarReleaseIdentity accepted)
    {
        try
        {
            await operations.InstallOrUpdateAsync(accepted, progress =>
            {
                lock (_operationGate)
                {
                    _operation = _operation! with
                    {
                        State = progress.Message is "Verifying" or "Installing" or "Restarting" or "Stopping" ? progress.Message : "Downloading",
                        ReceivedBytes = progress.ReceivedBytes, TotalBytes = progress.TotalBytes
                    };
                }
            }, lifetime.ApplicationStopping).ConfigureAwait(false);
            lock (_operationGate) { _operation = _operation! with { State = "Succeeded" }; }
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException || !lifetime.ApplicationStopping.IsCancellationRequested)
            {
                logger.LogError(ex, "Local Avatar installation or replacement failed for release {Version}.", accepted.Version);
            }

            lock (_operationGate) { _operation = _operation! with { State = "Failed", Error = ex.Message }; }
        }
        finally
        {
            backup.ReleaseAdmission();
            InstallationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Waits for owned work before its dependent singleton services are disposed.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_operationTask is not null) { await _operationTask.ConfigureAwait(false); }

        _checkGate.Dispose();
    }
}
