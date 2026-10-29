using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Oscova.Agents.Core.ApiContracts.Updates;
using Oscova.Agents.Core.Backup;
using Oscova.Agents.Core.Plugins.Registry;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Core.Updates;
using Oscova.Agents.Infrastructure.Backup;
using Oscova.Agents.Infrastructure.Plugins.Registry;
using Oscova.Agents.Infrastructure.Utils;
using Oscova.Agents.Plugins;

namespace Oscova.Agents.Infrastructure.Updates;

/// <summary>
/// Prepares the environment at the first start of a new version, or of the same version for a plugin-only update.
/// The attempt record is written before the checkpoint so a crash at any point leaves evidence outside the data root.
/// ChangesBegun is recorded only after the checkpoint is verified, so recovery knows whether the original data changed.
/// </summary>
public sealed class ApplicationUpdatePreparation
{
    private readonly IConfiguration _configuration;
    private readonly Action<string>? _report;
    // Plugin identifier to the failure message of its update, or null when the update was applied.
    private readonly Dictionary<string, string?> _pluginUpdates = new(StringComparer.OrdinalIgnoreCase);

    private ApplicationUpdatePreparation(ApplicationUpdateOperation? operation, IConfiguration configuration, Action<string>? report)
    {
        Operation = operation;
        _configuration = configuration;
        _report = report;
    }

    /// <summary>Gets the attempt this start prepares, or null for an ordinary start.</summary>
    public ApplicationUpdateOperation? Operation { get; private set; }

    /// <summary>
    /// Resolves the attempt and, before database startup, either stages its recovery or creates its verified checkpoint.
    /// </summary>
    /// <param name="previousVersion">The version recorded in the environment marker when it differs from this version.</param>
    /// <param name="currentVersion">The application version this process runs.</param>
    /// <param name="configuration">The Host configuration that locates the default database.</param>
    /// <param name="report">Receives progress lines for the Host console, or null.</param>
    /// <param name="cancellationToken">Cancels the preparation.</param>
    public static async Task<ApplicationUpdatePreparation> PrepareEnvironmentAsync(
        string? previousVersion,
        string currentVersion,
        IConfiguration configuration,
        Action<string>? report,
        CancellationToken cancellationToken)
    {
        var latest = ApplicationUpdateFiles.ResolveOperation();
        if (latest is { IsUnfinished: true, State: ApplicationUpdateState.Restoring })
        {
            if (!ApplicationUpdateFiles.VersionsEqual(latest.SourceVersion, currentVersion))
            {
                throw new InvalidOperationException(
                    $"Version {latest.SourceVersion} must be installed to finish restoring this environment. Run 'oscova update --version {latest.SourceVersion}'.");
            }

            // A pending operation means recovery was interrupted, so the restore bootstrapper resumes it.
            if (!EnvironmentRestoreBootstrapper.HasPendingOperation())
            {
                report?.Invoke($"Restoring the environment of Oscova {latest.SourceVersion} from its update checkpoint.");
                await StageRecoveryAsync(latest, configuration, cancellationToken).ConfigureAwait(false);
            }

            return new ApplicationUpdatePreparation(latest, configuration, report);
        }

        if (latest is { IsUnfinished: true, ChangesBegunReached: true } &&
            ApplicationUpdateFiles.VersionsEqual(latest.SourceVersion, currentVersion))
        {
            throw new InvalidOperationException(
                $"The update from {latest.SourceVersion} to {latest.TargetVersion} changed this environment and did not finish. " +
                $"Run 'oscova update --version {latest.SourceVersion}' to restore it.");
        }

        var operation = FindResumable(latest, previousVersion, currentVersion);
        if (operation is null && previousVersion is not null)
        {
            operation = ApplicationUpdateFiles.StartOperation(previousVersion, currentVersion, EnvironmentDataFormat.CurrentVersion);
        }

        var preparation = new ApplicationUpdatePreparation(operation, configuration, report);
        // A plugin-only attempt takes its checkpoint only after discovery finds a plugin update.
        if (operation is not null && !IsPluginOnly(operation))
        {
            await preparation.EnsureCheckpointAsync(cancellationToken).ConfigureAwait(false);
        }

        return preparation;
    }

    /// <summary>
    /// Reinstalls plugin binaries after a recovery, or applies every compatible plugin update for any other attempt.
    /// A plugin failure is reported and never blocks startup.
    /// </summary>
    public async Task UpdatePluginsAsync(
        IPluginRegistryService registry,
        IPluginInstallService installer,
        PendingPluginUpdateBootstrapper pendingUpdates,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (Operation is null)
        {
            return;
        }

        if (Operation.State == ApplicationUpdateState.Restoring)
        {
            await ReinstallRecoveredPluginsAsync(installer, pendingUpdates, logger, cancellationToken).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<InstalledPluginUpdate> updates;
        try
        {
            updates = await NuGetPluginRegistryService.FindInstalledUpdatesAsync(
                registry, PathUtil.PluginsPath, PluginFrameworkCompatibility.CurrentVersion.ToNormalizedString(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Plugin update discovery failed during application update {OperationId}.", Operation.OperationId);
            _report?.Invoke($"Plugin update check could not complete: {ex.Message}");
            if (IsPluginOnly(Operation))
            {
                ApplicationUpdateFiles.MarkFailed(Operation, ex.Message);
            }

            return;
        }

        if (IsPluginOnly(Operation))
        {
            if (updates.Count == 0)
            {
                ApplicationUpdateFiles.DeleteOperation(Operation.OperationId);
                Operation = null;
                return;
            }

            try
            {
                await EnsureCheckpointAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The data was not changed, so the current plugins keep running and the failure is shown afterwards.
                logger.LogError(ex, "The plugin update checkpoint failed for application update {OperationId}.", Operation.OperationId);
                _report?.Invoke($"Plugin updates were skipped because the update checkpoint failed: {ex.Message}");
                return;
            }
        }

        foreach (var update in updates)
        {
            _report?.Invoke($"Updating plugin {update.Name}: {update.InstalledVersion} -> {update.Version}");
            try
            {
                await installer.InstallAsync(update.PackageId, update.Version, update.PluginId, cancellationToken).ConfigureAwait(false);
                _pluginUpdates[update.PluginId] = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Plugin {PluginId} could not be updated to {Version}.", update.PluginId, update.Version);
                _report?.Invoke($"Plugin {update.Name} could not be updated: {ex.Message}");
                _pluginUpdates[update.PluginId] = ex.Message;
            }
        }

        pendingUpdates.ApplyPendingUpdates();
    }

    /// <summary>Records the final state of every installed plugin after compatibility admission.</summary>
    public void RecordPluginOutcomes(PluginActivationSettings pluginSettings)
    {
        if (Operation is null)
        {
            return;
        }

        var outcomes = new List<PluginUpdateOutcome>();
        foreach (var inspection in AgentPluginLoader.InspectDirectory(PathUtil.PluginsPath))
        {
            if (inspection.Manifest is not { } manifest)
            {
                continue;
            }

            var disabledReason = pluginSettings.CompatibilityDisabledReasons.GetValueOrDefault(manifest.Id) ?? inspection.Diagnostic?.Message;
            if (disabledReason is not null)
            {
                outcomes.Add(new PluginUpdateOutcome(manifest.Id, manifest.Name, "Disabled", manifest.Version, disabledReason));
            }
            else if (_pluginUpdates.TryGetValue(manifest.Id, out var failure) && failure is null)
            {
                outcomes.Add(new PluginUpdateOutcome(manifest.Id, manifest.Name, "Updated", manifest.Version, null));
            }
            else
            {
                outcomes.Add(new PluginUpdateOutcome(manifest.Id, manifest.Name, "Retained", manifest.Version, failure));
            }
        }

        Operation.PluginOutcomes = outcomes;
        ApplicationUpdateFiles.WriteOperation(Operation);
    }

    /// <summary>
    /// Finishes the attempt once the Host is ready, and removes the previous tree of an applied restore.
    /// Returns true when an update or recovery finished with this start.
    /// </summary>
    public bool Complete(Action<Exception> cleanupFailed)
    {
        EnvironmentRestoreBootstrapper.CompletePendingOperation(cleanupFailed);
        switch (Operation?.State)
        {
            case ApplicationUpdateState.Restoring:
                ApplicationUpdateFiles.Advance(Operation, ApplicationUpdateState.Restored);
                return true;
            case ApplicationUpdateState.ChangesBegun:
                ApplicationUpdateFiles.Advance(Operation, ApplicationUpdateState.Completed);
                ApplicationUpdateFiles.ApplyRetention(Operation.OperationId);
                return true;
            default:
                // A failed plugin-only attempt left the data unchanged and stays Failed for the dialog.
                return false;
        }
    }

    /// <summary>Returns true when the attempt keeps the application version and only replaces plugins.</summary>
    public static bool IsPluginOnly(ApplicationUpdateOperation operation) =>
        ApplicationUpdateFiles.VersionsEqual(operation.SourceVersion, operation.TargetVersion);

    private static ApplicationUpdateOperation? FindResumable(
        ApplicationUpdateOperation? latest,
        string? previousVersion,
        string currentVersion)
    {
        if (latest is not { IsUnfinished: true } ||
            latest.State == ApplicationUpdateState.Restoring ||
            !ApplicationUpdateFiles.VersionsEqual(latest.TargetVersion, currentVersion) ||
            previousVersion is not null && !ApplicationUpdateFiles.VersionsEqual(latest.SourceVersion, previousVersion))
        {
            return null;
        }

        // A failed plugin-only attempt changed nothing. Retrying it at every start would repeat the failure.
        return IsPluginOnly(latest) && latest.State == ApplicationUpdateState.Failed ? null : latest;
    }

    private async Task EnsureCheckpointAsync(CancellationToken cancellationToken)
    {
        var operation = Operation!;
        if (operation.ChangesBegunReached)
        {
            // The checkpoint already holds the original data and must never be replaced by partially changed data.
            if (operation.State == ApplicationUpdateState.Failed)
            {
                ApplicationUpdateFiles.Advance(operation, ApplicationUpdateState.ChangesBegun);
            }

            return;
        }

        try
        {
            if (operation.State == ApplicationUpdateState.Failed)
            {
                operation.FailedPhase = null;
                operation.Diagnostic = null;
                ApplicationUpdateFiles.Advance(operation, ApplicationUpdateState.Started);
            }

            _report?.Invoke($"Creating the update checkpoint for Oscova {operation.SourceVersion}.");
            var directory = ApplicationUpdateFiles.GetOperationDirectory(operation.OperationId);
            foreach (var name in new[] { ApplicationUpdateFiles.BackupFileName, ApplicationUpdateFiles.BackupFileName + ".tmp", ApplicationUpdateFiles.RecoveryKeyFileName })
            {
                File.Delete(Path.Combine(directory, name));
            }

            var descriptor = ValidateCheckpointEnvironment(operation);
            await CreateCheckpointAsync(operation, descriptor, cancellationToken).ConfigureAwait(false);
            ApplicationUpdateFiles.Advance(operation, ApplicationUpdateState.CheckpointVerified);
            WriteRecoveryInstructions(operation);
            ApplicationUpdateFiles.Advance(operation, ApplicationUpdateState.ChangesBegun);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ApplicationUpdateFiles.MarkFailed(operation, ex.Message);
            throw;
        }
    }

    private static EnvironmentDataFormat ValidateCheckpointEnvironment(ApplicationUpdateOperation operation)
    {
        var descriptor = JsonSerializer.Deserialize<EnvironmentDataFormat>(
            File.ReadAllBytes(Path.Combine(PathUtil.AppDataPath, EnvironmentDataFormat.FileName)), JsonSerializerOptions.Web);
        if (descriptor?.DataFormatVersion != EnvironmentDataFormat.CurrentVersion ||
            !ApplicationUpdateFiles.VersionsEqual(descriptor.AppVersion, operation.SourceVersion))
        {
            throw new InvalidOperationException("The source environment format is missing or unsupported.");
        }

        var restorableEntries = Directory.EnumerateFileSystemEntries(PathUtil.AppDataPath)
            .Where(path => !EnvironmentRestoreBootstrapper.IsRetainedTopLevelEntry(Path.GetFileName(path)))
            .ToArray();
        long bytes = 0;
        foreach (var entry in restorableEntries)
        {
            var paths = Directory.Exists(entry)
                ? Directory.EnumerateFileSystemEntries(entry, "*", SearchOption.AllDirectories).Prepend(entry)
                : [entry];
            foreach (var path in paths)
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException($"Environment entry '{path}' cannot be a link during an application update.");
                }

                if (File.Exists(path))
                {
                    bytes += new FileInfo(path).Length;
                }
            }
        }

        // The checkpoint, its verification copy and a later restore each need room next to the original data.
        var requiredBytes = bytes > long.MaxValue / 4 ? long.MaxValue : bytes * 4;
        // Existing directories identify their mounted filesystem, which can differ from the path root on Unix.
        if (new DriveInfo(ApplicationUpdateFiles.UpdatesDirectory).AvailableFreeSpace < requiredBytes ||
            new DriveInfo(PathUtil.AppDataPath).AvailableFreeSpace < requiredBytes)
        {
            throw new IOException("The update drive does not have enough free space for the checkpoint and its recovery copies.");
        }

        return descriptor;
    }

    private async Task CreateCheckpointAsync(
        ApplicationUpdateOperation operation,
        EnvironmentDataFormat descriptor,
        CancellationToken cancellationToken)
    {
        var directory = ApplicationUpdateFiles.GetOperationDirectory(operation.OperationId);
        var keyPath = Path.Combine(directory, ApplicationUpdateFiles.RecoveryKeyFileName);
        var backupPath = Path.Combine(directory, ApplicationUpdateFiles.BackupFileName);
        var temporaryBackupPath = backupPath + ".tmp";
        var passphrase = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await File.WriteAllTextAsync(keyPath, passphrase, cancellationToken).ConfigureAwait(false);
        ApplicationUpdateFiles.ProtectFile(keyPath);

        using var backup = new EnvironmentBackupService(_configuration, NullLogger<EnvironmentBackupService>.Instance);
        var output = new FileStream(temporaryBackupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await using (output.ConfigureAwait(false))
        {
            await backup.ExportAsync(output, passphrase, "application-update", descriptor, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }

        File.Move(temporaryBackupPath, backupPath);
        ApplicationUpdateFiles.ProtectFile(backupPath);
        _report?.Invoke("Verifying the update checkpoint.");
        var input = new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using (input.ConfigureAwait(false))
        {
            await EnvironmentBackupService.VerifyAsync(input, passphrase, cancellationToken).ConfigureAwait(false);
        }

        ApplicationUpdateFiles.AppendLog(operation.OperationId, $"Checkpoint verified: {new FileInfo(backupPath).Length} bytes.");
    }

    private static async Task StageRecoveryAsync(
        ApplicationUpdateOperation operation,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        try
        {
            var directory = ApplicationUpdateFiles.GetOperationDirectory(operation.OperationId);
            var passphrase = (await File.ReadAllTextAsync(
                Path.Combine(directory, ApplicationUpdateFiles.RecoveryKeyFileName), cancellationToken).ConfigureAwait(false)).Trim();
            using var backup = new EnvironmentBackupService(configuration, NullLogger<EnvironmentBackupService>.Instance);
            var input = new FileStream(Path.Combine(directory, ApplicationUpdateFiles.BackupFileName), FileMode.Open, FileAccess.Read, FileShare.Read);
            await using (input.ConfigureAwait(false))
            {
                await backup.StageCheckpointRestoreAsync(input, passphrase, cancellationToken).ConfigureAwait(false);
            }

            ApplicationUpdateFiles.AppendLog(operation.OperationId, "Recovery staged.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The attempt stays Restoring so the next start retries the same recovery.
            ApplicationUpdateFiles.AppendLog(operation.OperationId, "Recovery staging failed: " + ex.Message);
            throw;
        }
    }

    // The checkpoint keeps plugin inventory without binaries, so recovery downloads each recorded package version.
    private async Task ReinstallRecoveredPluginsAsync(
        IPluginInstallService installer,
        PendingPluginUpdateBootstrapper pendingUpdates,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        foreach (var inspection in AgentPluginLoader.InspectDirectory(PathUtil.PluginsPath))
        {
            if (inspection is not { Manifest: { } manifest, Diagnostic: not null } ||
                InstalledPluginPackage.Read(inspection) is not { } package)
            {
                continue;
            }

            _report?.Invoke($"Reinstalling plugin {manifest.Name} {package.Version}.");
            try
            {
                await installer.InstallAsync(package.PackageId, package.Version, manifest.Id, cancellationToken,
                    explicitSelection: package.ExplicitSelection).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Plugin {PluginId} {Version} could not be reinstalled after recovery.", manifest.Id, package.Version);
                _report?.Invoke($"Plugin {manifest.Name} could not be reinstalled: {ex.Message}");
            }
        }

        pendingUpdates.ApplyPendingUpdates();
    }

    private static void WriteRecoveryInstructions(ApplicationUpdateOperation operation)
    {
        var rootAssignment = OperatingSystem.IsWindows()
            ? $"$env:{PathUtil.AppDataDirectoryEnvironmentVariable} = {QuotePowerShell(operation.AppDataPath)}"
            : $"export {PathUtil.AppDataDirectoryEnvironmentVariable}={QuotePosix(operation.AppDataPath)}";
        var path = Path.Combine(
            ApplicationUpdateFiles.GetOperationDirectory(operation.OperationId),
            ApplicationUpdateFiles.RecoveryInstructionsFileName);
        File.WriteAllText(path, string.Join(Environment.NewLine,
            $"# Oscova update recovery: {operation.SourceVersion} to {operation.TargetVersion}.",
            "# Use these commands only if Oscova does not start after the update. Run them in order in a terminal.",
            "# 1. Point the commands at your Oscova data:",
            rootAssignment,
            "# 2. Show what happened to the update:",
            "oscova update status",
            $"# 3. Install version {operation.SourceVersion} and restore its data:",
            $"oscova update --version {operation.SourceVersion}",
            "# If the oscova command itself does not run, reinstall the previous version first, then repeat step 3:",
            $"dotnet tool update --global {NuGetApplicationUpdateSource.PackageId} --version {operation.SourceVersion} --allow-downgrade --source {NuGetApplicationUpdateSource.ResolvePackageSource()}",
            string.Empty));
        ApplicationUpdateFiles.ProtectFile(path);
    }

    private static string QuotePowerShell(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string QuotePosix(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
}
