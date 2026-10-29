using System.Diagnostics;
using System.Text.Json;
using Oscova.Agents.Core.Backup;
using Oscova.Agents.Core.Cli;
using Oscova.Agents.Infrastructure.Utils;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Infrastructure.Backup;

/// <summary>
/// Applies and recovers staged environment restores before startup opens persisted state.
/// </summary>
public static class EnvironmentRestoreBootstrapper
{
    private const string ResetCompletionFileName = "reset-completed.json";
    private static readonly TimeSpan ProcessExitWait = TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    /// <summary>
    /// Prevents two processes from mutating the same environment.
    /// </summary>
    public static FileStream AcquireEnvironmentLease()
    {
        Directory.CreateDirectory(PathUtil.AppDataPath);
        try
        {
            return new FileStream(PathUtil.EnvironmentLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"Environment '{PathUtil.AppDataPath}' is already open in another Oscova process.", ex);
        }
    }

    /// <summary>
    /// Returns whether an environment activation journal exists.
    /// </summary>
    public static bool HasPendingOperation() => File.Exists(GetOperationPath());

    /// <summary>Reads the current environment operation when one exists.</summary>
    public static PendingEnvironmentOperation? ReadPendingOperationOrNull() =>
        HasPendingOperation() ? ReadOperation(GetOperationPath()) : null;

    /// <summary>
    /// Applies a prepared restore and retains the previous environment until startup succeeds.
    /// </summary>
    public static void ApplyPendingOperationIfPresent()
    {
        var markerPath = GetOperationPath();
        if (!File.Exists(markerPath))
        {
            return;
        }

        var operation = ReadOperation(markerPath);
        if (operation.Phase == EnvironmentRestorePhase.Committed)
        {
            CleanupCommittedOperation(operation);
            return;
        }

        // Partial moves require explicit recovery so the Host never starts against a split environment.
        if (operation.Phase != EnvironmentRestorePhase.Prepared)
        {
            throw new InvalidOperationException(BuildRecoveryRequiredMessage(operation.Mode, operation.Phase));
        }

        WaitForInitiatingProcess(operation.InitiatingProcessId);
        if (operation.Mode == EnvironmentOperationMode.Restore)
        {
            ValidateExactInventory(PathUtil.RestoreStagingPath, operation.StagedEntries, "staged", operation.Mode);
        }

        var previousPath = GetPreviousPath(operation);
        ValidateResetBackupDirectory(operation, previousPath);
        EnsureRecoveryAreaIsEmpty(previousPath);

        operation.PreviousEntries = GetRestorableEntries(PathUtil.AppDataPath, operation.Mode);
        operation.Phase = EnvironmentRestorePhase.MovingPrevious;
        WriteOperation(operation);
        foreach (var name in operation.PreviousEntries)
        {
            MoveWithoutOverwrite(Path.Combine(PathUtil.AppDataPath, name), Path.Combine(previousPath, name));
        }

        if (operation.Mode == EnvironmentOperationMode.Restore)
        {
            operation.Phase = EnvironmentRestorePhase.MovingStaged;
            WriteOperation(operation);
            foreach (var name in operation.StagedEntries)
            {
                MoveWithoutOverwrite(Path.Combine(PathUtil.RestoreStagingPath, name), Path.Combine(PathUtil.AppDataPath, name));
            }
        }

        operation.Phase = EnvironmentRestorePhase.AwaitingHealth;
        WriteOperation(operation);
    }

    /// <summary>
    /// Commits an activated environment and removes its retained previous copy.
    /// </summary>
    public static void CompletePendingOperation(Action<Exception>? cleanupFailed = null)
    {
        var markerPath = GetOperationPath();
        if (!File.Exists(markerPath))
        {
            return;
        }

        var operation = ReadOperation(markerPath);
        if (operation.Phase != EnvironmentRestorePhase.AwaitingHealth &&
            operation.Phase != EnvironmentRestorePhase.Committed)
        {
            throw new InvalidOperationException(BuildRecoveryRequiredMessage(operation.Mode, operation.Phase));
        }

        if (operation.Phase != EnvironmentRestorePhase.Committed)
        {
            operation.Phase = EnvironmentRestorePhase.Committed;
            WriteOperation(operation);
        }

        try
        {
            CleanupCommittedOperation(operation);
        }
        catch (Exception ex) when (
            operation.Mode == EnvironmentOperationMode.Restore &&
            ex is IOException or UnauthorizedAccessException)
        {
            cleanupFailed?.Invoke(ex);
        }
    }

    /// <summary>
    /// Restores the retained previous environment after an interrupted activation.
    /// </summary>
    public static void RecoverPendingOperation()
    {
        var markerPath = GetOperationPath();
        if (!File.Exists(markerPath))
        {
            throw new InvalidOperationException("No interrupted environment restore is available to recover.");
        }

        var operation = ReadOperation(markerPath);
        if (operation.Phase == EnvironmentRestorePhase.Committed)
        {
            throw new InvalidOperationException("The pending environment restore was already committed.");
        }

        if (operation.Phase is EnvironmentRestorePhase.MovingStaged or EnvironmentRestorePhase.AwaitingHealth)
        {
            operation.Phase = EnvironmentRestorePhase.RecoveringStaged;
            WriteOperation(operation);
        }

        if (operation.Phase == EnvironmentRestorePhase.RecoveringStaged)
        {
            EvacuateFailedCandidate(operation.Mode);
            operation.Phase = EnvironmentRestorePhase.RecoveringPrevious;
            WriteOperation(operation);
        }
        else if (operation.Phase is EnvironmentRestorePhase.Prepared or EnvironmentRestorePhase.MovingPrevious)
        {
            operation.Phase = EnvironmentRestorePhase.RecoveringPrevious;
            WriteOperation(operation);
        }

        if (operation.Phase != EnvironmentRestorePhase.RecoveringPrevious)
        {
            throw new InvalidOperationException($"Pending environment restore phase '{operation.Phase}' cannot be recovered.");
        }

        var previousPath = GetPreviousPath(operation);
        ValidateResetBackupDirectory(operation, previousPath);
        RestorePreviousEntries(previousPath, operation.PreviousEntries, operation.Mode);
        var recoveredPath = Path.Combine(PathUtil.PendingOperationDirectory, "recovered.json");
        if (File.Exists(recoveredPath))
        {
            throw new InvalidOperationException($"Recovery evidence already exists at '{recoveredPath}'.");
        }

        File.Move(markerPath, recoveredPath);
        DeleteDirectoryIfEmpty(previousPath);
    }

    /// <summary>Returns whether an entry remains in place while restorable data is activated.</summary>
    public static bool IsRetainedTopLevelEntry(string name)
    {
        return string.Equals(name, "models", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "voices", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "logs", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "skill-registry-cache", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, PathUtil.PluginPackageCacheFolderName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "keys", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, ".environment.lock", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "_pending", StringComparison.OrdinalIgnoreCase);
    }

    internal static void PublishPreparedOperation(PendingEnvironmentOperation operation)
    {
        if (HasPendingOperation() || HasRecoveryEvidence())
        {
            throw new EnvironmentBackupException(
                "Another environment restore or recovery is already pending. Resolve it before importing another backup.");
        }

        operation.Phase = EnvironmentRestorePhase.Prepared;
        ValidateInventory(operation.StagedEntries, "staged", operation.Mode);
        WriteOperation(operation);
    }

    /// <summary>Returns the retained sibling path for a validated Reset operation.</summary>
    public static string GetResetBackupPath(PendingEnvironmentOperation operation)
    {
        if (operation.Mode != EnvironmentOperationMode.Reset ||
            !Guid.TryParseExact(operation.ResetOperationId, "N", out _) ||
            operation.ResetCreatedAtUtc is not { } createdAt ||
            createdAt == default ||
            createdAt.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("The pending environment reset identity is invalid.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(PathUtil.AppDataPath));
        var parent = Directory.GetParent(root)?.FullName
            ?? throw new InvalidOperationException("The environment reset destination cannot be resolved.");
        var name = $"{Path.GetFileName(root)}.reset-{createdAt:yyyyMMdd-HHmmss}-{operation.ResetOperationId}";
        var destination = Path.GetFullPath(Path.Combine(parent, name));
        if (!PathHelper.IsPathWithinDirectory(destination, parent))
        {
            throw new InvalidOperationException("The environment reset destination is invalid.");
        }

        return destination;
    }

    /// <summary>Returns whether the latest committed reset receipt matches the supplied identity.</summary>
    public static bool IsResetCompleted(string operationId)
    {
        if (!Guid.TryParseExact(operationId, "N", out _))
        {
            return false;
        }

        var path = GetResetCompletionPath();
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("operationId", out var value) &&
                value.ValueKind == JsonValueKind.String &&
                string.Equals(value.GetString(), operationId, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static void CancelPreparedOperation()
    {
        var markerPath = GetOperationPath();
        if (!File.Exists(markerPath))
        {
            return;
        }

        var operation = ReadOperation(markerPath);
        if (operation.Phase != EnvironmentRestorePhase.Prepared)
        {
            throw new InvalidOperationException("An environment restore already started and must be recovered offline.");
        }

        if (operation.Mode == EnvironmentOperationMode.Reset)
        {
            var backupPath = GetResetBackupPath(operation);
            if (Directory.Exists(backupPath) && Directory.EnumerateFileSystemEntries(backupPath).Any())
            {
                throw new InvalidOperationException(
                    "The prepared reset directory is no longer empty. Recovery evidence was retained.");
            }

            DeleteDirectoryIfEmpty(backupPath);
            File.Delete(markerPath);
            DeleteDirectoryIfEmpty(PathUtil.PendingOperationDirectory);
            return;
        }

        Directory.Delete(PathUtil.PendingOperationDirectory, recursive: true);
    }

    /// <summary>Returns whether retained recovery evidence blocks another environment operation.</summary>
    public static bool HasRecoveryEvidence()
    {
        return Directory.Exists(PathUtil.RestoreFailedPath) ||
            File.Exists(Path.Combine(PathUtil.PendingOperationDirectory, "recovered.json"));
    }

    private static void EnsureRecoveryAreaIsEmpty(string previousPath)
    {
        if (Directory.Exists(previousPath) &&
            Directory.EnumerateFileSystemEntries(previousPath).Any())
        {
            throw new InvalidOperationException("Previous environment recovery data already exists. Refusing to overwrite it.");
        }

        if (HasRecoveryEvidence())
        {
            throw new InvalidOperationException("Recovered environment evidence must be archived and removed before another restore.");
        }

        CliFileSecurity.EnsurePrivateDirectory(previousPath);
    }

    private static PendingEnvironmentOperation ReadOperation(string markerPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(markerPath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw InvalidJournal();
            }

            if (!HasKind(root, "mode", JsonValueKind.Number) ||
                !HasKind(root, "initiatingProcessId", JsonValueKind.Number) ||
                !HasKind(root, "phase", JsonValueKind.Number) ||
                !HasKind(root, "previousEntries", JsonValueKind.Array) ||
                !HasKind(root, "stagedEntries", JsonValueKind.Array))
            {
                throw InvalidJournal();
            }

            var operation = root.Deserialize<PendingEnvironmentOperation>(JsonOptions) ?? throw InvalidJournal();
            if (!Enum.IsDefined(operation.Mode) ||
                !Enum.IsDefined(operation.Phase))
            {
                throw InvalidJournal();
            }

            if (operation.Mode == EnvironmentOperationMode.Restore)
            {
                if (!root.TryGetProperty("manifest", out var manifest) ||
                    manifest.ValueKind != JsonValueKind.Object ||
                    !HasKind(manifest, "bundleFormatVersion", JsonValueKind.Number) ||
                    !HasKind(manifest, "dataFormatVersion", JsonValueKind.Number) ||
                    !HasKind(manifest, "appVersion", JsonValueKind.String) ||
                    !HasKind(manifest, "exportedAtUtc", JsonValueKind.String) ||
                    !HasKind(manifest, "exportedByUserId", JsonValueKind.String) ||
                    operation.Manifest is not { } restoreManifest ||
                    restoreManifest.BundleFormatVersion != EnvironmentBundleManifest.CurrentBundleFormatVersion ||
                    restoreManifest.DataFormatVersion != EnvironmentDataFormat.CurrentVersion ||
                    string.IsNullOrWhiteSpace(restoreManifest.AppVersion) ||
                    restoreManifest.ExportedAtUtc == default ||
                    restoreManifest.ExportedAtUtc.Offset != TimeSpan.Zero)
                {
                    throw InvalidJournal();
                }
            }
            else if (operation.Manifest is not null ||
                !Guid.TryParseExact(operation.ResetOperationId, "N", out _) ||
                operation.ResetCreatedAtUtc is not { } resetCreatedAt ||
                resetCreatedAt == default ||
                resetCreatedAt.Offset != TimeSpan.Zero ||
                !root.TryGetProperty("resetRestartArguments", out var restartArguments) ||
                restartArguments.ValueKind != JsonValueKind.Array ||
                restartArguments.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String) ||
                operation.ResetRestartArguments is null ||
                operation.StagedEntries.Count != 0)
            {
                throw InvalidJournal();
            }

            ValidateInventory(operation.PreviousEntries, "previous", operation.Mode);
            ValidateInventory(operation.StagedEntries, "staged", operation.Mode);
            return operation;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "Pending environment operation journal is invalid. Keep it intact and stage the restore again with the matching application.", ex);
        }

        static bool HasKind(JsonElement value, string name, JsonValueKind kind) =>
            value.TryGetProperty(name, out var property) && property.ValueKind == kind;
        static InvalidOperationException InvalidJournal() => new(
            "Pending environment operation journal is invalid. Keep it intact and stage the restore again with the matching application.");
    }

    private static void WriteOperation(PendingEnvironmentOperation operation)
    {
        Directory.CreateDirectory(PathUtil.PendingOperationDirectory);
        var markerPath = GetOperationPath();
        var temporaryPath = markerPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, operation, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, markerPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void WaitForInitiatingProcess(int processId)
    {
        if (processId <= 0 || processId == Environment.ProcessId)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.WaitForExit(ProcessExitWait))
            {
                throw new InvalidOperationException(
                    $"The process that prepared this restore ({processId}) did not exit within {ProcessExitWait.TotalSeconds:0} seconds. No environment files were changed.");
            }
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException ex) when (!ex.Message.StartsWith("The process that prepared", StringComparison.Ordinal))
        {
        }
    }

    private static IReadOnlyList<string> GetRestorableEntries(string rootPath, EnvironmentOperationMode mode)
    {
        if (!Directory.Exists(rootPath))
        {
            return [];
        }

        var entries = Directory.EnumerateFileSystemEntries(rootPath)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name) && !IsRetainedEntry(name!, mode))
            .Cast<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();
        ValidateInventory(entries, "environment", mode);
        ValidateNoReparsePoints(rootPath, entries);
        return entries;
    }

    private static void ValidateExactInventory(
        string rootPath,
        IReadOnlyList<string> expected,
        string label,
        EnvironmentOperationMode mode)
    {
        ValidateInventory(expected, label, mode);
        var actual = GetRestorableEntries(rootPath, mode);
        if (!actual.SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"The {label} environment inventory changed after validation.");
        }
    }

    private static void ValidateInventory(
        IReadOnlyList<string> entries,
        string label,
        EnvironmentOperationMode mode)
    {
        var caseInsensitiveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in entries)
        {
            if (string.IsNullOrWhiteSpace(name) ||
                name is "." or ".." ||
                Path.IsPathRooted(name) ||
                !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal) ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                name.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                name.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal) ||
                IsRetainedEntry(name, mode) ||
                !caseInsensitiveNames.Add(name))
            {
                throw new InvalidOperationException($"The {label} environment inventory contains an unsafe entry name.");
            }
        }
    }

    private static void ValidateNoReparsePoints(string rootPath, IEnumerable<string> entries)
    {
        foreach (var name in entries)
        {
            var path = Path.Combine(rootPath, name);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"Environment entry '{name}' cannot be a link.");
            }
        }
    }

    private static void EvacuateFailedCandidate(EnvironmentOperationMode mode)
    {
        Directory.CreateDirectory(PathUtil.RestoreFailedPath);
        foreach (var name in GetRestorableEntries(PathUtil.AppDataPath, mode))
        {
            MoveWithoutOverwrite(Path.Combine(PathUtil.AppDataPath, name), Path.Combine(PathUtil.RestoreFailedPath, name));
        }

        if (!Directory.Exists(PathUtil.RestoreStagingPath))
        {
            return;
        }

        foreach (var name in GetRestorableEntries(PathUtil.RestoreStagingPath, mode))
        {
            MoveWithoutOverwrite(Path.Combine(PathUtil.RestoreStagingPath, name), Path.Combine(PathUtil.RestoreFailedPath, name));
        }
    }

    private static void RestorePreviousEntries(
        string previousPath,
        IReadOnlyList<string> previousEntries,
        EnvironmentOperationMode mode)
    {
        ValidateInventory(previousEntries, "previous", mode);
        foreach (var name in previousEntries)
        {
            var source = Path.Combine(previousPath, name);
            var destination = Path.Combine(PathUtil.AppDataPath, name);
            var sourceExists = EntryExists(source);
            var destinationExists = EntryExists(destination);
            if (sourceExists && destinationExists)
            {
                throw new InvalidOperationException($"Recovery found two copies of '{name}' and will not overwrite either one.");
            }

            if (sourceExists)
            {
                MoveEntry(source, destination);
            }
            else if (!destinationExists)
            {
                throw new InvalidOperationException($"Recovery cannot find the previous environment entry '{name}'.");
            }
        }
    }

    private static void MoveWithoutOverwrite(string source, string destination)
    {
        if (!EntryExists(source))
        {
            throw new InvalidOperationException($"Environment entry '{source}' is missing.");
        }

        if (EntryExists(destination))
        {
            throw new InvalidOperationException($"Environment entry '{destination}' already exists.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        MoveEntry(source, destination);
    }

    private static void MoveEntry(string source, string destination)
    {
        if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination);
        }
    }

    private static bool EntryExists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void CleanupCommittedOperation(PendingEnvironmentOperation operation)
    {
        if (operation.Mode == EnvironmentOperationMode.Reset)
        {
            WriteResetCompletion(operation.ResetOperationId!);
        }
        else
        {
            DeleteDirectoryIfPresent(PathUtil.RestorePreviousPath);
            DeleteDirectoryIfPresent(PathUtil.RestoreStagingPath);
        }

        var markerPath = GetOperationPath();
        if (File.Exists(markerPath))
        {
            File.Delete(markerPath);
        }

        DeleteDirectoryIfEmpty(PathUtil.PendingOperationDirectory);
    }

    private static void DeleteDirectoryIfPresent(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void DeleteDirectoryIfEmpty(string path)
    {
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
        {
            Directory.Delete(path);
        }
    }

    private static string GetOperationPath() =>
        Path.Combine(PathUtil.PendingOperationDirectory, PendingEnvironmentOperation.FileName);

    private static string GetPreviousPath(PendingEnvironmentOperation operation) =>
        operation.Mode == EnvironmentOperationMode.Reset
            ? GetResetBackupPath(operation)
            : PathUtil.RestorePreviousPath;

    private static void ValidateResetBackupDirectory(PendingEnvironmentOperation operation, string backupPath)
    {
        if (operation.Mode != EnvironmentOperationMode.Reset)
        {
            return;
        }

        if (!Directory.Exists(backupPath) ||
            Directory.GetParent(backupPath) is null ||
            EnvironmentBackupService.HasLinkInAncestorChain(backupPath))
        {
            throw new InvalidOperationException(
                "The retained reset destination changed or contains a link. Recovery evidence was retained.");
        }
    }

    private static string GetResetCompletionPath() =>
        Path.Combine(PathUtil.PendingOperationDirectory, ResetCompletionFileName);

    private static bool IsRetainedEntry(string name, EnvironmentOperationMode mode) =>
        IsRetainedTopLevelEntry(name) &&
        (mode != EnvironmentOperationMode.Reset ||
         !string.Equals(name, "keys", StringComparison.OrdinalIgnoreCase));

    private static void WriteResetCompletion(string operationId)
    {
        var path = GetResetCompletionPath();
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new { operationId }, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    /// <summary>Describes recovery of an interrupted operation against the selected environment root.</summary>
    public static string BuildRecoveryRequiredMessage(EnvironmentOperationMode mode, EnvironmentRestorePhase phase)
    {
        var root = Path.GetFullPath(PathUtil.AppDataPath);
        var command = OperatingSystem.IsWindows()
            ? $"$env:{PathUtil.AppDataDirectoryEnvironmentVariable} = '{root.Replace("'", "''", StringComparison.Ordinal)}'; oscova environment recover --yes"
            : $"{PathUtil.AppDataDirectoryEnvironmentVariable}='{root.Replace("'", "'\"'\"'", StringComparison.Ordinal)}' oscova environment recover --yes";
        var operation = mode == EnvironmentOperationMode.Reset ? "reset" : "restore";
        return $"Environment {operation} stopped during '{phase}'. Stop Oscova and recover '{root}' with: {command}";
    }
}
