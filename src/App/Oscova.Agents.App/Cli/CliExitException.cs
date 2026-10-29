using Microsoft.AspNetCore.Connections;
using Oscova.Agents.Core.Backup;
using Oscova.Agents.Host.Hosting;
using Oscova.Agents.Infrastructure.Backup;
using Oscova.Agents.Infrastructure.Utils;

namespace Oscova.Agents.App.Cli;

public sealed class CliExitException(int exitCode, string message) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

public static class CliExceptionRenderer
{
    /// <summary>Formats safe startup details for both the foreground terminal and detached startup pipe.</summary>
    public static string FormatStartupFailure(Exception exception)
    {
        var diagnosis = exception is DatabaseStartupException
            ? exception.Message
            : FindPortConflict(exception) ?? "Oscova startup failed.";
        var message = $"{diagnosis}\nDetails: {PathUtil.SystemLogsPath}";
        if (exception is not DatabaseStartupException) { return message; }

        try
        {
            // Only an interrupted activation has retained data that environment recovery can restore.
            var pending = EnvironmentRestoreBootstrapper.ReadPendingOperationOrNull();
            if (pending is { Phase: EnvironmentRestorePhase.AwaitingHealth })
            {
                message += $"\n{EnvironmentRestoreBootstrapper.BuildRecoveryRequiredMessage(pending.Mode, pending.Phase)}";
            }
        }
        catch (Exception) { /* An unreadable recovery journal must not hide the database diagnosis. */ }

        return message;
    }

    // Kestrel wraps a port conflict in an IOException that names the address, which the user can act on.
    private static string? FindPortConflict(Exception exception)
    {
        for (var current = exception; current.InnerException is { } inner; current = inner)
        {
            if (inner is AddressInUseException) { return current.Message; }
        }

        return null;
    }

    public static int Render(Exception exception)
    {
        // Direct commands share the same HTTP exit contract as HostCommandBase.
        if (exception is HttpRequestException { StatusCode: { } status })
        {
            exception = new CliExitException(CliHttpErrors.MapExitCode(status), $"The host answered HTTP {(int)status}.");
        }

        if (exception is CliExitException cliExit)
        {
            CliRender.Failure(cliExit.Message, HintFor(cliExit.ExitCode));
            return cliExit.ExitCode;
        }

        CliRender.Failure(exception.Message);
        return 1;
    }

    private static string? HintFor(int exitCode) => exitCode switch
    {
        2 => "Run 'oscova login' to sign this CLI in to the host.",
        3 => "Your account does not have permission for this operation.",
        4 => "Run 'oscova <command> --help' for usage.",
        _ => null
    };
}
