using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Oscova.Agents.Infrastructure.Data;

namespace Oscova.Agents.Host.Hosting;

/// <summary>Validates retained database history before the Host applies its migrations.</summary>
internal static class DatabaseStartup
{
    /// <summary>Prepares the database and preserves a safe diagnosis when preparation fails.</summary>
    internal static async Task MigrateAsync(
        ApplicationDbContext context, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            await ValidateHistoryAsync(context, cancellationToken).ConfigureAwait(false);
            await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            try { logger.LogError(exception, "Oscova database preparation failed."); }
            catch (Exception) { /* Logging failure must not replace the database diagnosis. */ }

            if (exception is DatabaseStartupException) { throw; }

            var message = exception switch
            {
                SqliteException sqlite => $"Oscova database preparation failed (SQLite error {sqlite.SqliteErrorCode}). Check the database and storage details in the system logs.",
                UnauthorizedAccessException => "Oscova database preparation failed. Check access permissions for the database and its directory.",
                _ => "Oscova database preparation failed. Check the system logs for the cause before retrying."
            };
            throw new DatabaseStartupException(message, exception);
        }
    }

    /// <summary>Rejects histories that cannot be advanced safely by this release.</summary>
    private static async Task ValidateHistoryAsync(ApplicationDbContext context, CancellationToken cancellationToken)
    {
        var known = context.Database.GetMigrations().ToArray();
        var applied = (await context.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToArray();
        const string remedy = "No database migration was attempted. Use a release compatible with this database, or obtain a corrected release with the required migrations.";
        if (!applied.SequenceEqual(known.Take(applied.Length), StringComparer.OrdinalIgnoreCase))
        {
            throw new DatabaseStartupException(
                $"This Oscova release cannot upgrade the existing database because its migration history is not supported. {remedy}",
                new InvalidOperationException($"Known migrations: {string.Join(", ", known)}. Applied migrations: {string.Join(", ", applied)}."));
        }

        if (applied.Length != 0) { return; }

        // An absent history table does not make an existing application schema a fresh database.
        var connection = context.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        try
        {
            if (openedHere) { await connection.OpenAsync(cancellationToken).ConfigureAwait(false); }

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT name FROM sqlite_master
                WHERE type = 'table' AND lower(name) NOT GLOB 'sqlite_*'
                    AND lower(name) NOT IN ('__efmigrationshistory', '__efmigrationslock')
                LIMIT 1;
                """;
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string table)
            {
                throw new DatabaseStartupException(
                    $"This Oscova release cannot upgrade the existing database because the existing database has application tables but no applied migration history. {remedy}",
                    new InvalidOperationException($"Application table without applied migration history: {table}."));
            }
        }
        finally
        {
            if (openedHere) { await connection.CloseAsync().ConfigureAwait(false); }
        }
    }
}
