using Microsoft.Data.Sqlite;
using Oscova.Agents.Infrastructure.Utils;

namespace Oscova.Agents.Infrastructure.Data;

/// <summary>
/// Resolves the application SQLite database location from the canonical app data root.
/// </summary>
public static class ApplicationDatabaseConnection
{
    private const string LegacyRelativeDataSource = "app_data/app.db";

    /// <summary>
    /// Builds the SQLite connection string used when no explicit database override is configured.
    /// </summary>
    public static string CreateDefaultConnectionString()
    {
        return CreateConnectionString(PathUtil.DatabasePath);
    }

    /// <summary>
    /// Resolves configured SQLite data sources to a consistent, app-data-rooted file path.
    /// </summary>
    public static string ResolveConnectionString(string? configuredConnectionString)
    {
        if (string.IsNullOrWhiteSpace(configuredConnectionString))
        {
            return CreateDefaultConnectionString();
        }

        var connectionStringBuilder = new SqliteConnectionStringBuilder(configuredConnectionString);
        var dataSource = connectionStringBuilder.DataSource;

        if (ShouldLeaveDataSourceUnchanged(dataSource))
        {
            return connectionStringBuilder.ToString();
        }

        connectionStringBuilder.DataSource = ResolveFileDataSource(dataSource);
        return connectionStringBuilder.ToString();
    }

    /// <summary>
    /// Creates the parent directory for a file-backed SQLite database before EF opens it.
    /// </summary>
    public static void EnsureDirectoryExists(string connectionString)
    {
        var connectionStringBuilder = new SqliteConnectionStringBuilder(connectionString);
        var dataSource = connectionStringBuilder.DataSource;

        if (ShouldLeaveDataSourceUnchanged(dataSource))
        {
            return;
        }

        var fullPath = Path.GetFullPath(dataSource);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static string CreateConnectionString(string dataSource)
    {
        // Microsoft.Data.Sqlite's 30-second Default Timeout already retries a locked write, so no busy timeout is set here.
        return new SqliteConnectionStringBuilder
        {
            DataSource = dataSource
        }.ToString();
    }

    private static string ResolveFileDataSource(string dataSource)
    {
        var normalizedDataSource = dataSource.Replace('\\', '/');
        if (string.Equals(normalizedDataSource, LegacyRelativeDataSource, StringComparison.Ordinal))
        {
            return PathUtil.DatabasePath;
        }

        if (Path.IsPathFullyQualified(dataSource))
        {
            return Path.GetFullPath(dataSource);
        }

        return Path.GetFullPath(Path.Combine(PathUtil.AppDataPath, dataSource));
    }

    private static bool ShouldLeaveDataSourceUnchanged(string dataSource)
    {
        return string.IsNullOrWhiteSpace(dataSource) ||
            string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase) ||
            dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
    }
}