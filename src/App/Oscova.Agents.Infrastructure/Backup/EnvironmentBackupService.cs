using Oscova.Agents.Infrastructure.Memory;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Backup;
using Oscova.Agents.Core.Cli;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Infrastructure.Data;
using Oscova.Agents.Infrastructure.Plugins.Registry;
using Oscova.Agents.Infrastructure.Utils;
using Oscova.Agents.Plugins;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Infrastructure.Backup;

/// <summary>
/// Creates encrypted whole-environment backups and stages validated restores for the next startup.
/// </summary>
public sealed class EnvironmentBackupService(
    IConfiguration configuration,
    ILogger<EnvironmentBackupService> logger) : IDisposable
{
    public const string BackupExtension = ".oscb";

    private const int EnvelopeFormatVersion = 1;
    private const int Pbkdf2Iterations = 600_000;
    private const int EncryptionKeyLength = 32;
    private const int EncryptionSaltLength = 32;
    private const int EncryptionNonceLength = 12;
    private const int EncryptionTagLength = 16;
    private const int EncryptionChunkSize = 1024 * 1024;
    private const int ZipSymlinkFileType = 0xA000;
    private const int ZipFileTypeMask = 0xF000;
    private const long MaxUncompressedZipSize = EnvironmentExportLimits.MaxUncompressedZipSizeBytes;
    private const int MaxManifestSize = 64 * 1024;

    private static readonly byte[] MagicBytes = Encoding.ASCII.GetBytes("OSCOVABK");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly SemaphoreSlim _admissionGate = new(1, 1);

    /// <summary>
    /// Writes an encrypted environment backup to the provided output stream.
    /// </summary>
    public Task ExportAsync(
        Stream output,
        string passphrase,
        string exportedByUserId,
        string appVersion,
        CancellationToken ct)
    {
        return ExportAsync(
            output,
            passphrase,
            exportedByUserId,
            new EnvironmentDataFormat
            {
                DataFormatVersion = EnvironmentDataFormat.CurrentVersion,
                AppVersion = appVersion
            },
            ct);
    }

    /// <summary>
    /// Writes an encrypted backup using the supplied cold source format metadata.
    /// </summary>
    public async Task ExportAsync(
        Stream output,
        string passphrase,
        string exportedByUserId,
        EnvironmentDataFormat sourceFormat,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(passphrase))
        {
            throw new EnvironmentBackupException("A passphrase is required.");
        }

        if (sourceFormat.DataFormatVersion <= 0 || string.IsNullOrWhiteSpace(sourceFormat.AppVersion))
        {
            throw new EnvironmentBackupException("The source environment format metadata is invalid.");
        }

        var tempZipPath = CreateTempFilePath(".zip");
        var tempDatabasePath = CreateTempFilePath(".db");

        try
        {
            await EncryptedVectorStorage.AccessGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await CopyDatabaseAsync(tempDatabasePath, ct).ConfigureAwait(false);
                await BuildZipAsync(tempZipPath, tempDatabasePath, exportedByUserId, sourceFormat, ct).ConfigureAwait(false);
            }
            finally
            {
                EncryptedVectorStorage.AccessGate.Release();
            }

            var plainZip = new FileStream(tempZipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using (plainZip.ConfigureAwait(false))
            {
                await EncryptAsync(plainZip, output, passphrase, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            DeleteTempFile(tempZipPath);
            DeleteTempFile(tempDatabasePath);
        }
    }

    /// <summary>
    /// Waits until restore preparation or first-account creation owns admission.
    /// </summary>
    public Task WaitForAdmissionAsync(CancellationToken cancellationToken) =>
        _admissionGate.WaitAsync(cancellationToken);

    /// <summary>Rejects a conflicting dashboard mutation without queueing it behind another operation.</summary>
    public Task<bool> TryAcquireAdmissionAsync(CancellationToken cancellationToken) =>
        _admissionGate.WaitAsync(0, cancellationToken);

    /// <summary>
    /// Releases restore and first-account admission.
    /// </summary>
    public void ReleaseAdmission() => _admissionGate.Release();

    /// <summary>
    /// Decrypts, validates, and stages an uploaded backup for the next startup.
    /// </summary>
    public Task StageRestoreAsync(Stream input, string passphrase, CancellationToken ct) =>
        StageRestoreCoreAsync(input, passphrase, trustedLocalCheckpoint: false, ct);

    /// <summary>
    /// Stages an application update checkpoint for activation by this process.
    /// This process created and verified the checkpoint, so the uploaded-backup expansion cap does not apply.
    /// </summary>
    public Task StageCheckpointRestoreAsync(Stream input, string passphrase, CancellationToken ct) =>
        StageRestoreCoreAsync(input, passphrase, trustedLocalCheckpoint: true, ct);

    /// <summary>Decrypts a backup and validates its manifest and every entry path without extracting it.</summary>
    public static async Task VerifyAsync(Stream input, string passphrase, CancellationToken ct)
    {
        var tempZipPath = CreateTempFilePath(".zip");
        try
        {
            var tempZip = new FileStream(tempZipPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            await using (tempZip.ConfigureAwait(false))
            {
                await DecryptAsync(input, tempZip, passphrase, ct).ConfigureAwait(false);
            }

            using var archive = ZipFile.OpenRead(tempZipPath);
            await ValidateManifestAsync(archive, ct).ConfigureAwait(false);
            CreateExtractionPlan(archive, long.MaxValue);
        }
        catch (InvalidDataException ex)
        {
            throw new EnvironmentBackupException("Incorrect passphrase or corrupted backup.", ex);
        }
        finally
        {
            DeleteTempFile(tempZipPath);
        }
    }

    private async Task StageRestoreCoreAsync(
        Stream input,
        string passphrase,
        bool trustedLocalCheckpoint,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(passphrase))
        {
            throw new EnvironmentBackupException("A passphrase is required.");
        }

        EnsureStandardDatabase("Restore");
        if (EnvironmentRestoreBootstrapper.HasPendingOperation() || EnvironmentRestoreBootstrapper.HasRecoveryEvidence())
        {
            throw new EnvironmentBackupException(
                "Another environment restore or recovery is already pending. Resolve it before importing another backup.");
        }

        var tempZipPath = CreateTempFilePath(".zip");
        var stagingPrepared = false;

        try
        {
            var tempZip = new FileStream(tempZipPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            await using (tempZip.ConfigureAwait(false))
            {
                await DecryptAsync(input, tempZip, passphrase, ct).ConfigureAwait(false);
            }

            PrepareStagingDirectory();
            stagingPrepared = true;
            using var archive = ZipFile.OpenRead(tempZipPath);
            var manifest = await ValidateManifestAsync(archive, ct).ConfigureAwait(false);
            var extractionItems = CreateExtractionPlan(archive, trustedLocalCheckpoint ? long.MaxValue : MaxUncompressedZipSize);
            await ExtractItemsAsync(extractionItems, ct).ConfigureAwait(false);
            await EnvironmentSnapshotImporter.PrepareAsync(PathUtil.RestoreStagingPath, manifest, ct).ConfigureAwait(false);
            var stagedEntries = Directory.EnumerateFileSystemEntries(PathUtil.RestoreStagingPath)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Cast<string>()
                .Order(StringComparer.Ordinal)
                .ToArray();
            EnvironmentRestoreBootstrapper.PublishPreparedOperation(new PendingEnvironmentOperation
            {
                InitiatingProcessId = Environment.ProcessId,
                Manifest = manifest,
                StagedEntries = stagedEntries
            });
            stagingPrepared = false;
        }
        catch (EnvironmentBackupException ex)
        {
            logger.LogWarning(ex, "Environment restore backup validation failed.");
            throw;
        }
        catch (InvalidDataException ex)
        {
            throw new EnvironmentBackupException("Incorrect passphrase or corrupted backup.", ex);
        }
        finally
        {
            DeleteTempFile(tempZipPath);
            if (stagingPrepared && Directory.Exists(PathUtil.RestoreStagingPath))
            {
                Directory.Delete(PathUtil.RestoreStagingPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// Removes a staged restore when the host cannot hand off to a replacement process.
    /// </summary>
    public static void ClearPendingRestore()
    {
        EnvironmentRestoreBootstrapper.CancelPreparedOperation();
    }

    /// <summary>Validates that a reset can retain this environment without touching protected state.</summary>
    public void ValidateResetDestination(IReadOnlyList<string> protectedPaths)
    {
        EnsureStandardDatabase("Reset");

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(PathUtil.AppDataPath));
        var parent = Directory.GetParent(root)?.FullName;
        if (parent is null || PathHelper.ArePathsEqual(root, parent) ||
            !Directory.Exists(root) ||
            HasLinkInAncestorChain(root))
        {
            throw new EnvironmentBackupException(
                "Reset requires an independent application data directory that is not a filesystem root or link.");
        }

        foreach (var protectedPath in protectedPaths)
        {
            if (string.IsNullOrWhiteSpace(protectedPath))
            {
                throw new EnvironmentBackupException("Reset could not validate protected Oscova storage.");
            }

            var normalized = Path.GetFullPath(protectedPath);
            if (HasLinkInAncestorChain(normalized))
            {
                throw new EnvironmentBackupException("Reset cannot use protected storage that contains a filesystem link.");
            }

            if (PathHelper.IsPathWithinDirectory(root, normalized) ||
                PathHelper.IsPathWithinDirectory(normalized, root))
            {
                throw new EnvironmentBackupException(
                    "Reset cannot use an application data directory that overlaps CLI, Avatar, update, or installed application files. " +
                    "Stop Oscova and select an independent OSCOVA_APP_DATA_DIR.");
            }
        }

        if (PathHelper.HasSymlinkInPath(PathUtil.PendingOperationDirectory, root))
        {
            throw new EnvironmentBackupException("Reset cannot use an environment operation directory that contains a link.");
        }
    }

    /// <summary>Reports whether an existing path or any of its ancestors is a filesystem link.</summary>
    internal static bool HasLinkInAncestorChain(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
        {
            var attributes = current.Attributes;
            if (attributes != (FileAttributes)(-1) &&
                (attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Stages a confirmed clean environment while retaining the previous data.</summary>
    public PendingEnvironmentOperation StageReset(
        IReadOnlyList<string> protectedPaths,
        IReadOnlyList<string> restartArguments)
    {
        ValidateResetDestination(protectedPaths);
        if (EnvironmentRestoreBootstrapper.HasPendingOperation() ||
            EnvironmentRestoreBootstrapper.HasRecoveryEvidence())
        {
            throw new EnvironmentBackupException(
                "Another environment operation or recovery is pending. Resolve it before resetting the environment.");
        }

        if (restartArguments.Any(argument => argument is null))
        {
            throw new EnvironmentBackupException("The Host restart arguments are invalid.");
        }

        var operation = new PendingEnvironmentOperation
        {
            Mode = EnvironmentOperationMode.Reset,
            InitiatingProcessId = Environment.ProcessId,
            ResetOperationId = Guid.NewGuid().ToString("N"),
            ResetCreatedAtUtc = DateTimeOffset.UtcNow,
            ResetRestartArguments = [.. restartArguments],
            StagedEntries = []
        };
        var backupPath = EnvironmentRestoreBootstrapper.GetResetBackupPath(operation);
        if (File.Exists(backupPath) || Directory.Exists(backupPath))
        {
            throw new EnvironmentBackupException("The retained reset destination already exists.");
        }

        try
        {
            CliFileSecurity.EnsurePrivateDirectory(backupPath);
            EnvironmentRestoreBootstrapper.PublishPreparedOperation(operation);
            return operation;
        }
        catch
        {
            if (Directory.Exists(backupPath) && !Directory.EnumerateFileSystemEntries(backupPath).Any())
            {
                Directory.Delete(backupPath);
            }

            throw;
        }
    }

    private async Task CopyDatabaseAsync(string destinationPath, CancellationToken ct)
    {
        var connectionString = ApplicationDatabaseConnection.ResolveConnectionString(
            configuration.GetConnectionString("DefaultConnection"));
        var connectionBuilder = new SqliteConnectionStringBuilder(connectionString);
        var dataSource = connectionBuilder.DataSource;

        if (string.IsNullOrWhiteSpace(dataSource) ||
            string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase) ||
            dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            throw new EnvironmentBackupException("The configured SQLite database cannot be exported as a file backup.");
        }

        connectionBuilder.Pooling = false;
        var source = new SqliteConnection(connectionBuilder.ToString());
        await using (source.ConfigureAwait(false))
        {
            await source.OpenAsync(ct).ConfigureAwait(false);

            var destination = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = destinationPath,
                Pooling = false
            }.ToString());
            await using (destination.ConfigureAwait(false))
            {
                await destination.OpenAsync(ct).ConfigureAwait(false);
                source.BackupDatabase(destination);
            }
        }
    }

    private static async Task BuildZipAsync(
        string zipPath,
        string databaseCopyPath,
        string exportedByUserId,
        EnvironmentDataFormat sourceFormat,
        CancellationToken ct)
    {
        var zipStream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        await using (zipStream.ConfigureAwait(false))
        {
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false);

            var manifestEntry = archive.CreateEntry(EnvironmentBundleManifest.EntryName, CompressionLevel.Optimal);
            var manifestStream = manifestEntry.Open();
            await using (manifestStream.ConfigureAwait(false))
            {
                var manifest = new EnvironmentBundleManifest
                {
                    DataFormatVersion = sourceFormat.DataFormatVersion,
                    AppVersion = sourceFormat.AppVersion,
                    ExportedAtUtc = DateTimeOffset.UtcNow,
                    ExportedByUserId = exportedByUserId
                };
                await JsonSerializer.SerializeAsync(manifestStream, manifest, JsonOptions, ct).ConfigureAwait(false);
            }

            await AddFileAsync(archive, databaseCopyPath, "app.db", ct).ConfigureAwait(false);

            if (!Directory.Exists(PathUtil.AppDataPath))
            {
                return;
            }

            foreach (var filePath in Directory.EnumerateFiles(PathUtil.AppDataPath))
            {
                var fileName = Path.GetFileName(filePath);
                if (EnvironmentRestoreBootstrapper.IsRetainedTopLevelEntry(fileName) || IsSqliteSidecar(fileName) || string.Equals(fileName, "app.db", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (HasReparsePoint(filePath))
                {
                    continue;
                }

                await AddFileAsync(archive, filePath, PathHelper.ToSlashSeparatedRelativePath(fileName), ct).ConfigureAwait(false);
            }

            foreach (var directoryPath in Directory.EnumerateDirectories(PathUtil.AppDataPath))
            {
                var directoryName = Path.GetFileName(directoryPath);
                if (EnvironmentRestoreBootstrapper.IsRetainedTopLevelEntry(directoryName) || HasReparsePoint(directoryPath))
                {
                    continue;
                }

                if (PathHelper.ArePathsEqual(directoryPath, PathUtil.PluginsPath))
                {
                    await AddPluginInventoryAsync(archive, directoryPath, ct).ConfigureAwait(false);
                    continue;
                }

                await AddDirectoryAsync(archive, directoryPath, directoryName, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Archives only each plugin's manifest and package record. Plugin binaries are reinstalled
    /// from NuGet, and plugin data lives under plugin-data, which is archived normally.
    /// </summary>
    private static async Task AddPluginInventoryAsync(ZipArchive archive, string pluginsDirectory, CancellationToken ct)
    {
        archive.CreateEntry(Path.GetFileName(pluginsDirectory) + "/");
        foreach (var pluginDirectory in Directory.EnumerateDirectories(pluginsDirectory))
        {
            if (Path.GetFileName(pluginDirectory).StartsWith('.') || HasReparsePoint(pluginDirectory))
            {
                continue;
            }

            foreach (var fileName in new[] { AgentPluginLoader.ManifestFileName, InstalledPluginPackage.FileName })
            {
                var filePath = Path.Combine(pluginDirectory, fileName);
                if (File.Exists(filePath) && !HasReparsePoint(filePath))
                {
                    var relativePath = Path.GetRelativePath(PathUtil.AppDataPath, filePath);
                    await AddFileAsync(archive, filePath, PathHelper.ToSlashSeparatedRelativePath(relativePath), ct).ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task AddDirectoryAsync(
        ZipArchive archive,
        string sourceDirectory,
        string topLevelName,
        CancellationToken ct)
    {
        var entries = Directory.EnumerateFileSystemEntries(sourceDirectory).ToArray();
        if (entries.Length == 0)
        {
            archive.CreateEntry(PathHelper.ToSlashSeparatedRelativePath(topLevelName) + "/");
            return;
        }

        foreach (var entryPath in entries)
        {
            if (HasReparsePoint(entryPath) || HasReparseAncestor(entryPath, sourceDirectory))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(PathUtil.AppDataPath, entryPath);
            if (Directory.Exists(entryPath))
            {
                archive.CreateEntry(PathHelper.ToSlashSeparatedRelativePath(relativePath) + "/");
                if (Directory.EnumerateFileSystemEntries(entryPath).Any())
                {
                    await AddDirectoryAsync(archive, entryPath, relativePath, ct).ConfigureAwait(false);
                }
            }
            else
            {
                await AddFileAsync(archive, entryPath, PathHelper.ToSlashSeparatedRelativePath(relativePath), ct).ConfigureAwait(false);
            }
        }
    }

    private static bool HasReparseAncestor(string filePath, string rootDirectory)
    {
        var root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = Directory.GetParent(Path.GetFullPath(filePath));
        while (current is not null && PathHelper.IsPathWithinDirectory(current.FullName, root))
        {
            if (HasReparsePoint(current.FullName))
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    private static async Task AddFileAsync(ZipArchive archive, string sourcePath, string entryName, CancellationToken ct)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        var entryStream = entry.Open();
        await using (entryStream.ConfigureAwait(false))
        {
            var fileStream = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            await using (fileStream.ConfigureAwait(false))
            {
                await fileStream.CopyToAsync(entryStream, ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task<EnvironmentBundleManifest> ValidateManifestAsync(ZipArchive archive, CancellationToken ct)
    {
        var manifestEntries = archive.Entries
            .Where(entry => string.Equals(entry.FullName, EnvironmentBundleManifest.EntryName, StringComparison.Ordinal))
            .ToArray();
        if (manifestEntries.Length != 1)
        {
            throw new EnvironmentBackupException("Backup must contain exactly one manifest.json.");
        }

        var manifestEntry = manifestEntries[0];
        if (manifestEntry.Length > MaxManifestSize)
        {
            throw new EnvironmentBackupException("Backup manifest exceeds the 64 KiB limit.");
        }

        var manifestStream = manifestEntry.Open();
        await using (manifestStream.ConfigureAwait(false))
        {
            var bytes = await ReadBoundedAsync(manifestStream, MaxManifestSize, ct).ConfigureAwait(false);
            try
            {
                using var document = JsonDocument.Parse(bytes);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("bundleFormatVersion", out var bundleVersion) ||
                    bundleVersion.ValueKind != JsonValueKind.Number ||
                    !bundleVersion.TryGetInt32(out var bundleFormatVersion) ||
                    !root.TryGetProperty("dataFormatVersion", out var dataVersion) ||
                    dataVersion.ValueKind != JsonValueKind.Number ||
                    !dataVersion.TryGetInt32(out var dataFormatVersion) ||
                    !root.TryGetProperty("appVersion", out var appVersion) ||
                    appVersion.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(appVersion.GetString()) ||
                    !root.TryGetProperty("exportedAtUtc", out var exportedAt) ||
                    exportedAt.ValueKind != JsonValueKind.String ||
                    !exportedAt.TryGetDateTimeOffset(out var exportedAtUtc) ||
                    exportedAtUtc == default ||
                    exportedAtUtc.Offset != TimeSpan.Zero ||
                    !root.TryGetProperty("exportedByUserId", out var exportedBy) ||
                    exportedBy.ValueKind != JsonValueKind.String)
                {
                    throw new EnvironmentBackupException("Backup manifest is invalid.");
                }

                if (bundleFormatVersion != EnvironmentBundleManifest.CurrentBundleFormatVersion)
                {
                    throw new EnvironmentBackupException("This backup bundle format is not supported by this release.");
                }

                if (dataFormatVersion != EnvironmentDataFormat.CurrentVersion)
                {
                    throw new EnvironmentBackupException("This snapshot data format is not supported by this release.");
                }

                return root.Deserialize<EnvironmentBundleManifest>(JsonOptions)
                    ?? throw new EnvironmentBackupException("Backup manifest is invalid.");
            }
            catch (JsonException ex)
            {
                throw new EnvironmentBackupException("Backup manifest is invalid.", ex);
            }
        }
    }

    private static IReadOnlyList<ZipArchiveExtractionItem> CreateExtractionPlan(ZipArchive archive, long maxUncompressedSize)
    {
        foreach (var entry in archive.Entries)
        {
            if (IsSymlinkEntry(entry))
            {
                throw new EnvironmentBackupException($"Backup entry '{entry.FullName}' is not safe to restore.");
            }
        }

        try
        {
            return ZipArchiveExtractionPlan.Create(
                archive,
                PathUtil.RestoreStagingPath,
                maxUncompressedSize,
                entry =>
                {
                    var normalized = PathHelper.ToSlashSeparatedRelativePath(entry.FullName);
                    if (string.IsNullOrEmpty(normalized) ||
                        string.Equals(normalized, EnvironmentBundleManifest.EntryName, StringComparison.Ordinal))
                    {
                        return null;
                    }

                    var topLevelName = normalized.Split('/', 2, StringSplitOptions.RemoveEmptyEntries)[0];
                    if (EnvironmentRestoreBootstrapper.IsRetainedTopLevelEntry(topLevelName) || IsSqliteSidecar(topLevelName))
                    {
                        return null;
                    }

                    return normalized;
                });
        }
        catch (ArgumentException ex)
        {
            throw new EnvironmentBackupException(ex.Message, ex);
        }
    }

    private static async Task ExtractItemsAsync(IReadOnlyList<ZipArchiveExtractionItem> items, CancellationToken ct)
    {
        foreach (var item in items)
        {
            if (item.IsDirectory)
            {
                Directory.CreateDirectory(item.DestinationPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(item.DestinationPath)!);
            var source = item.Entry.Open();
            await using (source.ConfigureAwait(false))
            {
                var destination = new FileStream(item.DestinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await using (destination.ConfigureAwait(false))
                {
                    await source.CopyToAsync(destination, ct).ConfigureAwait(false);
                }
            }

            if (HasReparsePoint(item.DestinationPath))
            {
                File.Delete(item.DestinationPath);
                throw new EnvironmentBackupException($"Backup entry '{item.Entry.FullName}' is not safe to restore.");
            }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumLength, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > maximumLength)
            {
                throw new EnvironmentBackupException("Backup manifest exceeds the 64 KiB limit.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }

    private static async Task EncryptAsync(Stream plaintext, Stream output, string passphrase, CancellationToken ct)
    {
        var salt = RandomNumberGenerator.GetBytes(EncryptionSaltLength);
        var key = DeriveKey(passphrase, salt, Pbkdf2Iterations);
        try
        {
            await output.WriteAsync(MagicBytes, ct).ConfigureAwait(false);
            await WriteInt32Async(output, EnvelopeFormatVersion, ct).ConfigureAwait(false);
            await output.WriteAsync(salt, ct).ConfigureAwait(false);
            await WriteInt32Async(output, Pbkdf2Iterations, ct).ConfigureAwait(false);

            var buffer = new byte[EncryptionChunkSize];
            while (true)
            {
                var read = await plaintext.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                var nonce = RandomNumberGenerator.GetBytes(EncryptionNonceLength);
                var ciphertext = new byte[read];
                var tag = new byte[EncryptionTagLength];
                using var aes = new AesGcm(key, EncryptionTagLength);
                aes.Encrypt(nonce, buffer.AsSpan(0, read), ciphertext, tag);

                await WriteInt32Async(output, read, ct).ConfigureAwait(false);
                await output.WriteAsync(nonce, ct).ConfigureAwait(false);
                await output.WriteAsync(tag, ct).ConfigureAwait(false);
                await output.WriteAsync(ciphertext, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static async Task DecryptAsync(Stream input, Stream plaintext, string passphrase, CancellationToken ct)
    {
        try
        {
            var magic = await ReadExactlyAsync(input, MagicBytes.Length, ct).ConfigureAwait(false);
            if (!magic.SequenceEqual(MagicBytes))
            {
                throw new EnvironmentBackupException("The selected file is not an Oscova backup.");
            }

            var envelopeVersion = await ReadInt32Async(input, ct).ConfigureAwait(false);
            if (envelopeVersion != EnvelopeFormatVersion)
            {
                throw new EnvironmentBackupException("This backup uses a newer encryption format.");
            }

            var salt = await ReadExactlyAsync(input, EncryptionSaltLength, ct).ConfigureAwait(false);
            var iterations = await ReadInt32Async(input, ct).ConfigureAwait(false);
            if (iterations <= 0 || iterations > Pbkdf2Iterations)
            {
                throw new EnvironmentBackupException("Incorrect passphrase or corrupted backup.");
            }

            var key = DeriveKey(passphrase, salt, iterations);
            try
            {
                while (true)
                {
                    var plaintextLength = await ReadOptionalInt32Async(input, ct).ConfigureAwait(false);
                    if (plaintextLength is null)
                    {
                        break;
                    }

                    if (plaintextLength.Value < 0 || plaintextLength.Value > EncryptionChunkSize)
                    {
                        throw new EnvironmentBackupException("Incorrect passphrase or corrupted backup.");
                    }

                    var nonce = await ReadExactlyAsync(input, EncryptionNonceLength, ct).ConfigureAwait(false);
                    var tag = await ReadExactlyAsync(input, EncryptionTagLength, ct).ConfigureAwait(false);
                    var ciphertext = await ReadExactlyAsync(input, plaintextLength.Value, ct).ConfigureAwait(false);
                    var buffer = new byte[plaintextLength.Value];
                    using var aes = new AesGcm(key, EncryptionTagLength);
                    aes.Decrypt(nonce, ciphertext, tag, buffer);
                    await plaintext.WriteAsync(buffer, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        catch (CryptographicException ex)
        {
            throw new EnvironmentBackupException("Incorrect passphrase or corrupted backup.", ex);
        }
        catch (EndOfStreamException ex)
        {
            throw new EnvironmentBackupException("Incorrect passphrase or corrupted backup.", ex);
        }
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt, int iterations)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            EncryptionKeyLength);
    }

    private static async Task WriteInt32Async(Stream stream, int value, CancellationToken ct)
    {
        var buffer = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        await stream.WriteAsync(buffer, ct).ConfigureAwait(false);
    }

    private static async Task<int> ReadInt32Async(Stream stream, CancellationToken ct)
    {
        var buffer = await ReadExactlyAsync(stream, sizeof(int), ct).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt32LittleEndian(buffer);
    }

    private static async Task<int?> ReadOptionalInt32Async(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[sizeof(int)];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct).ConfigureAwait(false);
            if (read == 0)
            {
                if (offset == 0)
                {
                    return null;
                }

                throw new EndOfStreamException();
            }

            offset += read;
        }

        return BinaryPrimitives.ReadInt32LittleEndian(buffer);
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int length, CancellationToken ct)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
        }

        return buffer;
    }

    private static void PrepareStagingDirectory()
    {
        if (Directory.Exists(PathUtil.RestoreStagingPath))
        {
            Directory.Delete(PathUtil.RestoreStagingPath, recursive: true);
        }

        Directory.CreateDirectory(PathUtil.RestoreStagingPath);
    }

    private void EnsureStandardDatabase(string operation)
    {
        try
        {
            var connectionString = ApplicationDatabaseConnection.ResolveConnectionString(
                configuration.GetConnectionString("DefaultConnection"));
            var builder = new SqliteConnectionStringBuilder(connectionString);
            var dataSource = builder.DataSource;
            if (string.IsNullOrWhiteSpace(dataSource) ||
                string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase) ||
                dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
                builder.Mode == SqliteOpenMode.Memory)
            {
                throw new EnvironmentBackupException(
                    $"{operation} requires the standard file-backed app.db in the selected environment directory.");
            }

            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!string.Equals(Path.GetFullPath(dataSource), Path.GetFullPath(PathUtil.DatabasePath), comparison))
            {
                throw new EnvironmentBackupException(
                    $"{operation} requires the standard file-backed app.db in the selected environment directory.");
            }
        }
        catch (EnvironmentBackupException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw new EnvironmentBackupException(
                $"{operation} requires the standard file-backed app.db in the selected environment directory.", ex);
        }
    }

    private static bool IsSqliteSidecar(string name)
    {
        return string.Equals(name, "app.db-wal", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "app.db-shm", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasReparsePoint(string path)
    {
        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }

    private static bool IsSymlinkEntry(ZipArchiveEntry entry)
    {
        var unixMode = (entry.ExternalAttributes >> 16) & ZipFileTypeMask;
        return unixMode == ZipSymlinkFileType;
    }

    private static string CreateTempFilePath(string extension)
    {
        CliFileSecurity.EnsurePrivateDirectory(PathUtil.PendingOperationDirectory);
        return Path.Combine(PathUtil.PendingOperationDirectory, $"{Guid.NewGuid():N}{extension}");
    }

    private static void DeleteTempFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Temporary-file cleanup is best-effort; export/restore failures are reported earlier.
        }
    }

    public void Dispose()
    {
        _admissionGate.Dispose();
    }
}
