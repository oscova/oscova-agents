using System.Reflection;
using Oscova.Agents.App.Cli;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Oscova.Agents.App.Commands;

/// <summary>
/// One diagnostic line: what was checked, how it went, and what to do about it.
/// </summary>
public sealed record CliDiagnostic(string Check, string State, string Detail, string? Remedy = null)
{
    public const string Pass = "pass";
    public const string Warn = "warn";
    public const string Fail = "fail";
    public const string Info = "info";
}

/// <summary>
/// Runs read-only diagnostics on the CLI, its host connection, and its credentials.
/// Reporting a broken environment is a successful run, so this command always exits 0.
/// </summary>
public sealed class DoctorCommand(CliSurfaceInspector inspector, CliHostResolver resolver, HostApiClient apiClient, CliPaths paths)
    : AsyncCommand<OscovaCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, OscovaCommandSettings settings, CancellationToken cancellationToken)
    {
        CliOutput.Configure(settings);
        var report = await inspector.InspectAsync(settings, cancellationToken).ConfigureAwait(false);
        var hostVersion = await VersionCommand.ReadHostVersionAsync(resolver, apiClient, settings, cancellationToken).ConfigureAwait(false);

        var checks = new List<CliDiagnostic>
        {
            new("CLI version", CliDiagnostic.Info, Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "dev"),
            new("Target host", CliDiagnostic.Info, report.TargetHostUrl),
            // A successful remote version read proves reachability without local ownership.
            report.HostRunning || hostVersion is not null
                ? new CliDiagnostic("Host reachable", CliDiagnostic.Pass, "the host is running")
                : new CliDiagnostic("Host reachable", CliDiagnostic.Fail, "no host answered", "oscova start"),
            hostVersion is null
                ? new CliDiagnostic("Host version", CliDiagnostic.Fail, "could not be read", "oscova start")
                : new CliDiagnostic("Host version", CliDiagnostic.Pass, $"{hostVersion.Version} (framework {hostVersion.FrameworkVersion})"),
            LocalControlCheck(report),
            report.Bearer.LoggedIn
                ? new CliDiagnostic("Logged in", CliDiagnostic.Pass, $"{report.Bearer.UserName} at {report.Bearer.HostUrl}")
                : new CliDiagnostic("Logged in", CliDiagnostic.Fail, "no CLI login for this host", "oscova login"),
            ExpiryCheck(report.Bearer),
            new("Config file", CliDiagnostic.Info, paths.ConfigFilePath)
        };

        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(checks, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var table = CliRender.Table("Check", "State", "Detail");
        foreach (var check in checks)
        {
            var detail = check.Remedy is null ? check.Detail : $"{check.Detail} - run '{check.Remedy}'";
            table.AddRow(
                CliRender.Identifier(check.Check),
                new Text(check.State, StateStyle(check.State)),
                CliRender.Value(detail));
        }

        CliRender.Separator($"Diagnostics ({checks.Count})");
        AnsiConsole.Write(table);
        return 0;
    }

    private static CliDiagnostic LocalControlCheck(CliSurfaceReport report)
    {
        if (report.LocalControlAvailable)
        {
            return new CliDiagnostic("Local control token", CliDiagnostic.Pass, "available for lifecycle commands");
        }

        return report.TargetIsLoopback
            ? new CliDiagnostic("Local control token", CliDiagnostic.Warn, "not available; lifecycle commands use the CLI login instead")
            : new CliDiagnostic("Local control token", CliDiagnostic.Info, "not applicable for a remote host");
    }

    private static CliDiagnostic ExpiryCheck(CliBearerState bearer)
    {
        if (!bearer.LoggedIn || bearer.ExpiresAtUtc is not DateTimeOffset expiry)
        {
            return new CliDiagnostic("Token expiry", CliDiagnostic.Info, "no expiry recorded");
        }

        var remaining = expiry - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return new CliDiagnostic("Token expiry", CliDiagnostic.Fail, $"expired on {expiry:yyyy-MM-dd}", "oscova login");
        }

        return remaining <= TimeSpan.FromDays(7)
            ? new CliDiagnostic("Token expiry", CliDiagnostic.Warn, $"expires on {expiry:yyyy-MM-dd}", "oscova login")
            : new CliDiagnostic("Token expiry", CliDiagnostic.Pass, $"expires on {expiry:yyyy-MM-dd}");
    }

    private static Style StateStyle(string state) => state switch
    {
        CliDiagnostic.Pass => new Style(CliTheme.Success),
        CliDiagnostic.Warn => new Style(CliTheme.Warning),
        CliDiagnostic.Fail => new Style(CliTheme.Danger),
        _ => CliTheme.Label
    };
}
