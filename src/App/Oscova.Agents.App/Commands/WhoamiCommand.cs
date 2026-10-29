using Oscova.Agents.App.Cli;
using Spectre.Console.Cli;

namespace Oscova.Agents.App.Commands;

public sealed class WhoamiCommand(CliSurfaceInspector inspector) : AsyncCommand<OscovaCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, OscovaCommandSettings settings, CancellationToken cancellationToken)
    {
        CliOutput.Configure(settings);
        var report = await inspector.InspectAsync(settings, cancellationToken).ConfigureAwait(false);

        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(report, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        CliRender.KeyValue("Identity",
        [
            ("CLI login", CliRender.State(FormatBearer(report.Bearer), report.Bearer.LoggedIn)),
            ("Local control", CliRender.State(FormatLocalControl(report), report.LocalControlAvailable)),
            ("Browser", CliRender.Value("sign-in is separate; the website cookie never applies to this terminal"))
        ]);

        return 0;
    }

    private static string FormatBearer(CliBearerState bearer)
    {
        if (!bearer.LoggedIn)
        {
            return "not logged in - run 'oscova login'";
        }

        var expiry = bearer.ExpiresAtUtc is null
            ? "no expiry"
            : $"expires {bearer.ExpiresAtUtc:yyyy-MM-dd}";
        return $"{bearer.UserName} at {bearer.HostUrl} ({expiry})";
    }

    private static string FormatLocalControl(CliSurfaceReport report)
    {
        if (!report.LocalControlAvailable)
        {
            return report.HostRunning ? "not available for target host" : "no local host running";
        }

        return report.Pid is int pid
            ? $"available (host pid {pid} on this machine)"
            : "available";
    }
}
