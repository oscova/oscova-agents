using System.Text.Json;
using Microsoft.Data.Sqlite;
using Oscova.Agents.Core.Backup;

namespace Oscova.Agents.Infrastructure.Backup;

/// <summary>
/// Prepares a supported staged snapshot for the current environment contract.
/// </summary>
internal static class EnvironmentSnapshotImporter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    /// <summary>
    /// Validates a current-format snapshot and writes its normalized format descriptor.
    /// </summary>
    public static async Task PrepareAsync(
        string stagingPath,
        EnvironmentBundleManifest manifest,
        CancellationToken cancellationToken)
    {
        if (manifest.DataFormatVersion != EnvironmentDataFormat.CurrentVersion)
        {
            throw new EnvironmentBackupException("This snapshot data format is not supported by this release.");
        }

        var databasePath = Path.Combine(stagingPath, "app.db");
        if (!File.Exists(databasePath))
        {
            throw new EnvironmentBackupException("Backup is missing app.db.");
        }

        if ((File.GetAttributes(databasePath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new EnvironmentBackupException("Backup database cannot be a link.");
        }

        HashSet<string> userIds;
        try
        {
            userIds = await ValidateDatabaseAsync(databasePath, cancellationToken).ConfigureAwait(false);
        }
        catch (EnvironmentBackupException)
        {
            throw;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException)
        {
            throw new EnvironmentBackupException("Backup database could not be validated.", ex);
        }
        finally
        {
            DeleteSqliteSidecar(databasePath + "-wal");
            DeleteSqliteSidecar(databasePath + "-shm");
        }

        ValidateUserDirectories(stagingPath, userIds);
        ValidateExistingDescriptor(stagingPath, manifest.DataFormatVersion.Value);

        await WriteDescriptorAsync(
            stagingPath,
            new EnvironmentDataFormat
            {
                DataFormatVersion = manifest.DataFormatVersion.Value,
                AppVersion = manifest.AppVersion
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HashSet<string>> ValidateDatabaseAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var integrity = connection.CreateCommand();
            await using (integrity.ConfigureAwait(false))
            {
                integrity.CommandText = "PRAGMA quick_check;";
                var reader = await integrity.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                        !string.Equals(reader.GetString(0), "ok", StringComparison.OrdinalIgnoreCase) ||
                        await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        throw new EnvironmentBackupException("Backup database failed its integrity check.");
                    }
                }
            }

            var foreignKeys = connection.CreateCommand();
            await using (foreignKeys.ConfigureAwait(false))
            {
                foreignKeys.CommandText = "PRAGMA foreign_key_check;";
                var reader = await foreignKeys.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        throw new EnvironmentBackupException("Backup database contains broken references.");
                    }
                }
            }

            var userIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var users = connection.CreateCommand();
            await using (users.ConfigureAwait(false))
            {
                users.CommandText = "SELECT Id FROM AspNetUsers;";
                var reader = await users.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        userIds.Add(reader.GetString(0));
                    }
                }
            }

            return userIds;
        }
    }

    private static void ValidateUserDirectories(string stagingPath, IReadOnlySet<string> userIds)
    {
        var usersPath = Path.Combine(stagingPath, "users");
        if (!Directory.Exists(usersPath))
        {
            return;
        }

        var orphanedDirectories = Directory.EnumerateDirectories(usersPath)
            .Select(path => new DirectoryInfo(path))
            .Where(directory => Guid.TryParse(directory.Name, out _) && !userIds.Contains(directory.Name))
            .Select(directory => directory.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (orphanedDirectories.Length > 0)
        {
            throw new EnvironmentBackupException(
                $"Backup contains user data without an owning account: {string.Join(", ", orphanedDirectories)}.");
        }
    }

    private static void ValidateExistingDescriptor(string stagingPath, int dataFormatVersion)
    {
        var descriptorPath = Path.Combine(stagingPath, EnvironmentDataFormat.FileName);
        if (!File.Exists(descriptorPath))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(descriptorPath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("dataFormatVersion", out var version) ||
                version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var descriptorVersion) ||
                descriptorVersion <= 0 ||
                !root.TryGetProperty("appVersion", out var descriptorAppVersion) ||
                descriptorAppVersion.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(descriptorAppVersion.GetString()))
            {
                throw new EnvironmentBackupException("Backup environment format descriptor is invalid.");
            }

            if (descriptorVersion != dataFormatVersion)
            {
                throw new EnvironmentBackupException("Backup environment format descriptor does not match its manifest.");
            }
        }
        catch (JsonException ex)
        {
            throw new EnvironmentBackupException("Backup environment format descriptor is invalid.", ex);
        }
    }

    private static async Task WriteDescriptorAsync(
        string stagingPath,
        EnvironmentDataFormat descriptor,
        CancellationToken cancellationToken)
    {
        var descriptorPath = Path.Combine(stagingPath, EnvironmentDataFormat.FileName);
        var temporaryPath = descriptorPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, descriptor, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, descriptorPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void DeleteSqliteSidecar(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
