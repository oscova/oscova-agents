using System.Text.Json;
using Microsoft.Data.Sqlite;
using NuGet.Versioning;
using Oscova.Agents.Core.ApiContracts.Updates;
using Oscova.Agents.Core.Backup;
using Oscova.Agents.Core.Updates;
using Oscova.Agents.Infrastructure.Backup;
using Oscova.Agents.Infrastructure.Data;
using Oscova.Agents.Infrastructure.Updates;
using Oscova.Agents.Infrastructure.Utils;
using Oscova.Agents.Infrastructure.Plugins.Registry;
using Oscova.Agents.Core.Plugins.Registry;

namespace Oscova.Agents.Host.Services;

/// <summary>Supplies the verified CLI handoff available to the Host update flow.</summary>
/// <param name="ToolInstallation">The global tool identity, or null when this Host was not installed as a global tool.</param>
/// <param name="StartConsole">Opens the update console for a target version and returns false when it runs without a terminal window.</param>
public sealed record ApplicationUpdateHostOptions(
    ApplicationToolIdentity? ToolInstallation,
    Func<string, CancellationToken, Task<bool>>? StartConsole);

/// <summary>Owns operator-facing application update checks and update console admission.</summary>
public sealed class ApplicationUpdateService(
    NuGetApplicationUpdateSource source,
    EnvironmentBackupService backupService,
    ApplicationUpdateHostOptions options,
    RuntimeVersionService runtimeVersionService,
    IConfiguration configuration,
    IServiceScopeFactory scopeFactory,
    ILogger<ApplicationUpdateService> logger) : IDisposable
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(6);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _latestVersion;
    private DateTimeOffset _lastCheckUtc;
    private bool _lastCheckFailed;
    private string? _checkError;
    private IReadOnlyList<InstalledPluginUpdate> _pluginUpdates = [];

    private string CurrentVersion => options.ToolInstallation?.PackageVersion ?? runtimeVersionService.GetSnapshot().Host.DisplayVersion;

    /// <summary>Returns cached release availability unless an operator requests a refresh.</summary>
    public async Task<ApplicationUpdateStatus> GetStatusAsync(bool refresh, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (FindUnacknowledgedSuccess() is null &&
                (refresh || _lastCheckUtc == default || DateTimeOffset.UtcNow - _lastCheckUtc >= CacheLifetime))
            {
                try
                {
                    _checkError = null;
                    _pluginUpdates = [];
                    _latestVersion = await source.GetLatestVersionAsync(ct).ConfigureAwait(false);
                    _pluginUpdates = await FindPluginUpdatesAsync(IsNewer(_latestVersion, CurrentVersion) ? _latestVersion : null, ct).ConfigureAwait(false);
                    _lastCheckFailed = false;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _lastCheckFailed = true;
                    logger.LogWarning(ex, "Application update check failed for target release {Version}.", _latestVersion);
                }

                _lastCheckUtc = DateTimeOffset.UtcNow;
            }

            return BuildStatus();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Validates one exact update and opens the update console that applies it with every listed plugin update.
    /// A target equal to the installed version is a plugin-only update and needs at least one plugin update.
    /// </summary>
    public async Task<ApplicationUpdateStatus> StartAsync(string targetVersion, CancellationToken ct)
    {
        if (!NuGetVersion.TryParse(targetVersion, out var target) || target.IsPrerelease)
        {
            throw new ApplicationUpdateException("The target version must be a stable release version.");
        }

        var admissionAcquired = false;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var identity = options.ToolInstallation
                ?? throw new ApplicationUpdateException("This installation must be updated from the terminal.");
            if (options.StartConsole is null)
            {
                throw new ApplicationUpdateException("This Host cannot start the application update console.");
            }

            if (!await backupService.TryAcquireAdmissionAsync(ct).ConfigureAwait(false))
            {
                throw new ApplicationUpdateConflictException("Another restore, Avatar operation or harness update is already pending.");
            }

            admissionAcquired = true;
            if (OperatingSystem.IsWindows() && !Environment.UserInteractive)
            {
                throw new ApplicationUpdateException("Managed Windows sessions must be updated from the terminal after the service is stopped.");
            }

            ValidateDefaultDatabase();
            ValidateEnvironmentDescriptor(identity.PackageVersion);
            if (EnvironmentRestoreBootstrapper.HasPendingOperation() || EnvironmentRestoreBootstrapper.HasRecoveryEvidence())
            {
                throw new ApplicationUpdateConflictException("Another restore or application update is already pending.");
            }

            var current = NuGetVersion.Parse(identity.PackageVersion);
            if (target < current)
            {
                throw new ApplicationUpdateException("The selected application release is older than the installed version.");
            }

            if (target == current)
            {
                IReadOnlyList<InstalledPluginUpdate> pluginUpdates;
                try
                {
                    pluginUpdates = await FindPluginUpdatesAsync(null, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new ApplicationUpdateException(ex.Message);
                }

                if (pluginUpdates.Count == 0)
                {
                    throw new ApplicationUpdateException("No application or plugin update is available.");
                }
            }
            else if (!await source.IsVersionListedAsync(targetVersion, ct).ConfigureAwait(false))
            {
                throw new ApplicationUpdateException("The selected application release is no longer available.");
            }

            // Once accepted, the browser connection no longer owns the console or package acquisition.
            var normalizedTarget = target.ToNormalizedString();
            bool visible;
            try
            {
                visible = await options.StartConsole(normalizedTarget, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Application update console could not be started for release {Version}.", normalizedTarget);
                throw new ApplicationUpdateException($"The update console could not be started. {ex.Message}", ex);
            }

            return new ApplicationUpdateStatus(identity.PackageVersion, normalizedTarget, false,
                "Accepted", visible ? "operation-pending" : "console-unavailable", null, identity.UpdatesDirectory);
        }
        finally
        {
            if (admissionAcquired)
            {
                backupService.ReleaseAdmission();
            }

            _gate.Release();
        }
    }

    private ApplicationUpdateStatus BuildStatus()
    {
        var currentVersion = CurrentVersion;
        if (FindUnacknowledgedSuccess() is { } succeeded)
        {
            return new ApplicationUpdateStatus(currentVersion, succeeded.TargetVersion, false, "Succeeded", null,
                succeeded.OperationId, ApplicationUpdateFiles.GetOperationDirectory(succeeded.OperationId))
            {
                PluginOutcomes = succeeded.PluginOutcomes,
                PluginsOnly = ApplicationUpdatePreparation.IsPluginOnly(succeeded)
            };
        }

        var available = IsNewer(_latestVersion, currentVersion) ? _latestVersion : null;
        // A failed or restored attempt from this version stays visible until a different release is discovered.
        var latest = ApplicationUpdateFiles.ResolveOperation();
        var failed = latest is { State: ApplicationUpdateState.Failed or ApplicationUpdateState.Restored } &&
            ApplicationUpdateFiles.VersionsEqual(latest.SourceVersion, currentVersion) &&
            (available is null || ApplicationUpdateFiles.VersionsEqual(latest.TargetVersion, available)) ? latest : null;
        var reason = _lastCheckFailed
            ? _checkError is null ? "check-unavailable" : "invalid-release-metadata"
            : options.ToolInstallation is null ? "unsupported-installation" : null;
        var updateAvailable = available is not null || _pluginUpdates.Count > 0;
        return new ApplicationUpdateStatus(
            currentVersion,
            available ?? (_pluginUpdates.Count > 0 ? currentVersion : null),
            updateAvailable && options.ToolInstallation is not null && !_lastCheckFailed,
            failed is not null ? "Failed" : _lastCheckFailed ? "Unavailable" : updateAvailable ? "Available" : "Current",
            reason,
            failed?.OperationId,
            options.ToolInstallation?.UpdatesDirectory)
        {
            PluginUpdates = _pluginUpdates,
            Diagnostic = failed?.Diagnostic ?? _checkError,
            CompletionAcknowledged = failed?.CompletionAcknowledged ?? false
        };
    }

    /// <summary>Returns the newest attempt when it completed into this version and the operator has not dismissed it.</summary>
    private ApplicationUpdateOperation? FindUnacknowledgedSuccess() =>
        ApplicationUpdateFiles.ResolveOperation() is { State: ApplicationUpdateState.Completed, CompletionAcknowledged: false } completed &&
        ApplicationUpdateFiles.VersionsEqual(completed.TargetVersion, CurrentVersion)
            ? completed
            : null;

    /// <summary>Acknowledges a finished result when the operator dismisses its result dialog.</summary>
    public async Task AcknowledgeAsync(string operationId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var operation = ApplicationUpdateFiles.ReadOperation(operationId);
            if (operation.State is not (ApplicationUpdateState.Completed or ApplicationUpdateState.Failed or ApplicationUpdateState.Restored))
            {
                throw new ApplicationUpdateException("Only a finished application update can be acknowledged.");
            }

            operation.CompletionAcknowledged = true;
            ApplicationUpdateFiles.WriteOperation(operation);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Lists installed plugin updates for the framework of a newer application release, or of this one.</summary>
    private async Task<IReadOnlyList<InstalledPluginUpdate>> FindPluginUpdatesAsync(string? applicationVersion, CancellationToken ct)
    {
        var frameworkVersion = PluginFrameworkCompatibility.CurrentVersion.ToNormalizedString();
        if (applicationVersion is not null)
        {
            try
            {
                frameworkVersion = await NuGetApplicationUpdateSource.GetFrameworkVersionAsync(applicationVersion, ct).ConfigureAwait(false);
            }
            catch (InvalidDataException ex)
            {
                _checkError = $"App release {applicationVersion}: {ex.Message}";
                throw;
            }
        }

        return await FindInstalledPluginUpdatesAsync(frameworkVersion, ct).ConfigureAwait(false);
    }
    /// <summary>Resolves the scoped registry for one completed metadata check.</summary>
    private async Task<IReadOnlyList<InstalledPluginUpdate>> FindInstalledPluginUpdatesAsync(string frameworkVersion, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        return await NuGetPluginRegistryService.FindInstalledUpdatesAsync(scope.ServiceProvider.GetRequiredService<IPluginRegistryService>(),
            PathUtil.PluginsPath, frameworkVersion, ct).ConfigureAwait(false);
    }

    private static bool IsNewer(string? candidate, string current) =>
        NuGetVersion.TryParse(candidate, out var candidateVersion) &&
        NuGetVersion.TryParse(current, out var currentVersion) &&
        candidateVersion > currentVersion;

    private void ValidateDefaultDatabase()
    {
        var connectionString = ApplicationDatabaseConnection.ResolveConnectionString(
            configuration.GetConnectionString("DefaultConnection"));
        var builder = new SqliteConnectionStringBuilder(connectionString);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.IsNullOrWhiteSpace(builder.DataSource) ||
            builder.Mode == SqliteOpenMode.Memory ||
            !string.Equals(Path.GetFullPath(builder.DataSource), Path.GetFullPath(PathUtil.DatabasePath), comparison))
        {
            throw new ApplicationUpdateException("Automatic update requires the standard file-backed app.db.");
        }
    }

    private static void ValidateEnvironmentDescriptor(string expectedAppVersion)
    {
        var path = Path.Combine(PathUtil.AppDataPath, EnvironmentDataFormat.FileName);
        try
        {
            var descriptor = JsonSerializer.Deserialize<EnvironmentDataFormat>(File.ReadAllBytes(path), JsonSerializerOptions.Web);
            if (descriptor?.DataFormatVersion != EnvironmentDataFormat.CurrentVersion ||
                !string.Equals(descriptor.AppVersion, expectedAppVersion, StringComparison.OrdinalIgnoreCase))
            {
                throw new ApplicationUpdateException("The current environment format cannot be resolved safely.");
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            throw new ApplicationUpdateException("The current environment format cannot be resolved safely.", ex);
        }
    }

    public void Dispose() => _gate.Dispose();
}

/// <summary>Reports invalid or unsupported update requests without hiding their reason.</summary>
public class ApplicationUpdateException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>Reports that another durable operation already owns update admission.</summary>
public sealed class ApplicationUpdateConflictException(string message) : ApplicationUpdateException(message);
