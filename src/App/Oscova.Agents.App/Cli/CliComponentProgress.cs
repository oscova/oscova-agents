using Oscova.Agents.Core.Plugins.Registry;
using Spectre.Console;

namespace Oscova.Agents.App.Cli;

/// <summary>Displays default-component progress during foreground startup.</summary>
public static class CliComponentProgress
{
    /// <summary>Runs installation with terminal-appropriate component feedback.</summary>
    public static async Task RunAsync(
        Func<Action<DefaultPluginInstallProgress>?, Task> install,
        OscovaCommandSettings settings)
    {
        if (settings.Json || settings.Quiet)
        {
            await install(null).ConfigureAwait(false);
            return;
        }

        var live = !settings.Json && !settings.Quiet && !settings.NoInteractive &&
            AnsiConsole.Profile.Capabilities.Interactive &&
            AnsiConsole.Profile.Capabilities.Ansi;
        var started = false;
        var installed = 0;
        var failed = 0;
        ProgressTask? overall = null;
        var components = new Dictionary<int, ProgressTask>();

        void Report(DefaultPluginInstallProgress report, ProgressContext? context)
        {
            if (!started)
            {
                if (context is null)
                {
                    CliOutput.Info("Installing selected plugins");
                }

                started = true;
            }

            var installing = report.State == DefaultPluginInstallState.Installing;
            // Components install concurrently, so completions arrive out of number order.
            var processed = installed + failed + (installing ? 0 : 1);
            if (context is not null)
            {
                overall ??= context.AddTask("Installing selected plugins", maxValue: report.Total);
                overall.MaxValue = report.Total;
                overall.Value = processed;
                overall.Description = "Selected plugins " +
                    $"({processed}/{report.Total} processed, {report.Total - processed} remaining)";

                if (!components.TryGetValue(report.Number, out var component))
                {
                    component = context.AddTask(
                        $"Installing {report.Number}/{report.Total}: {report.PackageId}".EscapeMarkup(),
                        maxValue: 1);
                    components.Add(report.Number, component);
                }

                component.Description = report.State switch
                {
                    DefaultPluginInstallState.Installed =>
                        $"[{new Style(CliTheme.Success).ToMarkup()}]Installed: {report.PackageId.EscapeMarkup()}[/]",
                    DefaultPluginInstallState.Failed =>
                        $"[{new Style(CliTheme.Danger).ToMarkup()}]Failed: {report.PackageId.EscapeMarkup()} " +
                        "(see application logs)[/]",
                    _ => $"Installing {report.Number}/{report.Total}: {report.PackageId}".EscapeMarkup()
                };
                component.Value = installing ? 0 : 1;
                context.Refresh();
            }
            else if (installing)
            {
                CliOutput.Info($"Installing {report.Number}/{report.Total}: {report.PackageId}");
            }

            if (report.State == DefaultPluginInstallState.Installed)
            {
                installed++;
                if (context is null)
                {
                    CliOutput.Info($"Installed: {report.PackageId}");
                }
            }
            else if (report.State == DefaultPluginInstallState.Failed)
            {
                failed++;
                if (context is null)
                {
                    CliOutput.Info($"Failed: {report.PackageId} (see application logs)");
                }
            }
        }

        if (live)
        {
            await AnsiConsole.Progress()
                .AutoClear(false)
                .Columns(
                    new SpinnerColumn { Style = CliTheme.Accent, CompletedText = " " },
                    new TaskDescriptionColumn { Alignment = Justify.Left },
                    new ProgressBarColumn
                    {
                        CompletedStyle = CliTheme.Accent,
                        FinishedStyle = CliTheme.AccentDim,
                        RemainingStyle = CliTheme.Muted
                    })
                .StartAsync(context => install(report => Report(report, context)))
                .ConfigureAwait(false);
        }
        else
        {
            await install(report => Report(report, null)).ConfigureAwait(false);
        }

        if (started)
        {
            var summary = $"Selected plugins: {installed} installed, {failed} failed." +
                (failed > 0 ? " Continuing startup." : string.Empty);
            if (live)
            {
                AnsiConsole.Write(CliRender.Card(
                    "Component setup",
                    new Text(summary, failed > 0 ? new Style(CliTheme.Warning) : new Style(CliTheme.Success)),
                    failed > 0 ? CliTheme.Warning : CliTheme.Success));
            }
            else
            {
                CliOutput.Info(summary);
            }
        }
    }
}
