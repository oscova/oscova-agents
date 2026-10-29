using System.Globalization;
using Oscova.Agents.Core.ApiContracts.Updates;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Oscova.Agents.App.Cli;

/// <summary>
/// Shared visual vocabulary for CLI commands so tables, panels, and failures look the same everywhere.
/// </summary>
public static class CliRender
{
    private const int ErrorConsoleWidth = 4096;

    /// <summary>Explains the separate renderer window before installation consent or download.</summary>
    public static void AvatarInstallNotice() => AnsiConsole.Write(Card("Avatar Engine",
        new Text("Avatar Engine runs in a separate Windows window. We recommend two monitors or a dedicated Windows renderer PC so you can keep using Oscova while the avatar is fullscreen."), CliTheme.Accent));

    /// <summary>Explains the supported Avatar renderer without pausing harness setup.</summary>
    public static void AvatarPlatformNotice() => AnsiConsole.Write(Card("Avatar Engine",
        new Markup("Avatar Engine currently runs on Windows.\nOn your Windows renderer, run [bold]oscova avatar install[/]."), CliTheme.Accent));

    /// <summary>Builds a themed card without writing it to the console.</summary>
    public static Panel Card(string title, IRenderable content, Color accent) =>
        new(content)
        {
            Header = new PanelHeader($"[{CliTheme.HeadingMarkup}]{title.EscapeMarkup()}[/]"),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(accent),
            Padding = new Padding(1, 0, 1, 0)
        };

    /// <summary>Displays activity while awaiting an operation without a result.</summary>
    public static Task StatusAsync(
        string description, OscovaCommandSettings settings, Func<Task> operation) =>
        StatusAsync(description, settings, async () =>
        {
            await operation().ConfigureAwait(false);
            return true;
        });

    /// <summary>Displays activity while awaiting one caller-owned operation.</summary>
    public static async Task<T> StatusAsync<T>(
        string description, OscovaCommandSettings settings, Func<Task<T>> operation)
    {
        var showSpinner = !settings.Json && !settings.Quiet &&
            AnsiConsole.Profile.Capabilities.Interactive;

        return showSpinner
            ? await AnsiConsole.Status().StartAsync(
                description.EscapeMarkup(),
                async _ => await operation().ConfigureAwait(false)).ConfigureAwait(false)
            : await operation().ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a themed table with the supplied column headers.
    /// </summary>
    public static Spectre.Console.Table Table(params string[] columns)
    {
        var table = new Spectre.Console.Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(CliTheme.Border)
            .Expand();

        foreach (var column in columns)
        {
            table.AddColumn(new TableColumn(new Text(column, CliTheme.Heading)).PadRight(2));
        }

        return table;
    }

    /// <summary>
    /// Renders an opaque identifier cell, in full so it can be copied into another command.
    /// </summary>
    public static Text Identifier(string value) => new(value, CliTheme.Label);

    /// <summary>
    /// Renders an ordinary value cell.
    /// </summary>
    public static Text Value(string value) => new(value, CliTheme.Value);

    /// <summary>
    /// Renders an on/off cell, green when on and muted when off.
    /// </summary>
    public static Text State(string value, bool on) => new(value, on ? new Style(CliTheme.Success) : CliTheme.Label);

    /// <summary>
    /// Renders a status word, colouring known failure and idle states.
    /// </summary>
    public static Text Status(string value) => new(value, value.ToLowerInvariant() switch
    {
        "active" or "running" or "connected" or "enabled" or "ok" => new Style(CliTheme.Success),
        "inactive" or "stopped" or "disconnected" or "disabled" or "idle" => CliTheme.Label,
        "loadfailed" or "invalidmanifest" or "failed" or "error" => new Style(CliTheme.Danger),
        _ => CliTheme.Value
    });

    /// <summary>
    /// Renders a titled panel of label/value pairs.
    /// </summary>
    public static void KeyValue(string title, IEnumerable<(string Key, string Value)> rows) =>
        KeyValue(title, rows.Select(row => (row.Key, Value(row.Value))));

    /// <summary>
    /// Renders a titled panel of label/value pairs where values carry their own styling.
    /// </summary>
    public static void KeyValue(string title, IEnumerable<(string Key, Text Value)> rows)
    {
        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().PadRight(2))
            .AddColumn();
        foreach (var (key, value) in rows)
        {
            grid.AddRow(new Text(key, CliTheme.Label), value);
        }

        AnsiConsole.Write(Card(title, grid, CliTheme.AccentDim));
    }

    /// <summary>
    /// Writes a success line.
    /// </summary>
    public static void Success(string message) => WriteLine(AnsiConsole.Console, message, new Style(CliTheme.Success));

    /// <summary>
    /// Writes a warning line.
    /// </summary>
    public static void Warning(string message) => WriteLine(AnsiConsole.Console, message, new Style(CliTheme.Warning));

    /// <summary>
    /// Writes a failure message, and an optional remediation hint, to stderr.
    /// </summary>
    public static void Failure(string message, string? hint = null)
    {
        var console = CreateErrorConsole();
        var capabilities = AnsiConsole.Profile.Capabilities;
        if (capabilities.Interactive && capabilities.Ansi && !Console.IsErrorRedirected)
        {
            console.Profile.Width = AnsiConsole.Profile.Width;
            console.Write(Card("Error", new Text(message), CliTheme.Danger));
            console.Profile.Width = ErrorConsoleWidth;
        }
        else
        {
            WriteLine(console, message, new Style(CliTheme.Danger));
        }

        if (!string.IsNullOrWhiteSpace(hint))
        {
            WriteLine(console, hint, CliTheme.Label);
        }
    }

    /// <summary>
    /// Writes a horizontal rule, optionally titled.
    /// </summary>
    public static void Separator(string? title = null)
    {
        var rule = string.IsNullOrWhiteSpace(title) ? new Rule() : new Rule(title.EscapeMarkup());
        rule.Style = CliTheme.Border;
        AnsiConsole.Write(rule);
    }

    /// <summary>Runs Avatar Engine installation with terminal-appropriate byte progress.</summary>
    public static async Task RunAvatarInstallAsync(
        Func<Action<AvatarDownloadProgress>?, CancellationToken, Task> install,
        OscovaCommandSettings settings,
        CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (settings.Json || settings.Quiet)
        {
            await install(null, operation.Token).ConfigureAwait(false);
            return;
        }

        var capabilities = AnsiConsole.Profile.Capabilities;
        var canReadRetryChoice = !settings.NoInteractive && capabilities.Interactive;
        var live = canReadRetryChoice && capabilities.Ansi;
        if (!live)
        {
            await install(report =>
            {
                CancelAvatarRetryIfRequested(
                    canReadRetryChoice && report.RetryIn is not null,
                    operation);
                var bytes = FormatAvatarBytes(report);
                if (report.RetryIn is not null)
                {
                    CliOutput.Info($"{bytes} {FormatAvatarRetry(report, canReadRetryChoice)}");
                    return;
                }

                if (report.Message is not null)
                {
                    CliOutput.Info($"{bytes} {report.Message}");
                    return;
                }

                if (report.TotalBytes > 0)
                {
                    var percentage = (int)Math.Min(100, report.ReceivedBytes * 100 / report.TotalBytes);
                    CliOutput.Info($"Downloading Avatar Engine: {percentage}% {bytes}");
                }
                else
                {
                    CliOutput.Info($"Downloading Avatar Engine: {bytes}");
                }
            }, operation.Token).ConfigureAwait(false);
            return;
        }

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new SpinnerColumn { Style = CliTheme.Accent, CompletedText = " " },
                new AvatarDownloadDescriptionColumn(),
                new ProgressBarColumn
                {
                    CompletedStyle = CliTheme.Accent,
                    FinishedStyle = CliTheme.Success,
                    RemainingStyle = CliTheme.Muted
                })
            .StartAsync(async context =>
            {
                ProgressTask? task = null;
                await install(report =>
                {
                    CancelAvatarRetryIfRequested(
                        canReadRetryChoice && report.RetryIn is not null,
                        operation);
                    var knownTotal = report.TotalBytes > 0;
                    var bytes = FormatAvatarBytes(report);
                    task ??= context.AddTask("Downloading Avatar Engine", maxValue: knownTotal ? report.TotalBytes : 1);
                    task.IsIndeterminate = !knownTotal;
                    if (knownTotal)
                    {
                        task.MaxValue = report.TotalBytes;
                        task.Value = Math.Min(report.ReceivedBytes, report.TotalBytes);
                    }

                    if (report.RetryIn is not null)
                    {
                        task.Description = $"{bytes} {FormatAvatarRetry(report, canReadRetryChoice)}";
                    }
                    else if (report.Message is not null)
                    {
                        task.Description = $"{bytes} {report.Message}";
                    }
                    else
                    {
                        task.Description = $"Downloading Avatar Engine {bytes}";
                    }

                    context.Refresh();
                }, operation.Token).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    private static void CancelAvatarRetryIfRequested(
        bool canReadRetryChoice,
        CancellationTokenSource operation)
    {
        if (!canReadRetryChoice)
        {
            return;
        }

        while (AnsiConsole.Console.Input.IsKeyAvailable())
        {
            if (AnsiConsole.Console.Input.ReadKey(intercept: true)?.Key == ConsoleKey.N)
            {
                operation.Cancel();
                operation.Token.ThrowIfCancellationRequested();
            }
        }
    }

    private static string FormatAvatarRetry(AvatarDownloadProgress report, bool showCancelChoice)
    {
        var seconds = (int)Math.Ceiling(report.RetryIn!.Value.TotalSeconds);
        var message = $"{report.Message} Retrying in {seconds} seconds.";
        return showCancelChoice ? $"{message} Press N (No) to cancel." : message;
    }

    private static string FormatAvatarBytes(AvatarDownloadProgress report) =>
        report.TotalBytes > 0
            ? $"({FormatBytes(report.ReceivedBytes)} / {FormatBytes(report.TotalBytes)})"
            : $"({FormatBytes(report.ReceivedBytes)})";

    /// <summary>Wraps Avatar download activity without displacing its progress bar.</summary>
    private sealed class AvatarDownloadDescriptionColumn : ProgressColumn
    {
        public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime) =>
            new Markup(task.Description?.Trim() ?? string.Empty)
                .Overflow(Overflow.Fold)
                .Justify(Justify.Left);
    }

    /// <summary>
    /// Gates a destructive command behind --yes or an interactive confirmation.
    /// </summary>
    public static void ConfirmDestructive(OscovaCommandSettings settings, bool yes, string description)
    {
        if (yes)
        {
            return;
        }

        if (settings.NoInteractive || !AnsiConsole.Profile.Capabilities.Interactive)
        {
            throw new CliExitException(4, $"{description} Pass --yes to confirm without a prompt.");
        }

        var prompt = new ConfirmationPrompt($"{description.EscapeMarkup()} Continue?")
        {
            DefaultValue = false
        };

        if (!AnsiConsole.Prompt(prompt))
        {
            throw new CliExitException(4, "Cancelled.");
        }
    }

    private static void WriteLine(IAnsiConsole console, string message, Style style)
    {
        console.Write(new Text(message, style));
        console.WriteLine();
    }

    private static string FormatBytes(long bytes) =>
        string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_048_576d:0.0} MB");

    /// <summary>
    /// Builds a stderr console that mirrors the ANSI capability already negotiated for stdout.
    /// </summary>
    private static IAnsiConsole CreateErrorConsole()
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiConsole.Profile.Capabilities.Ansi ? AnsiSupport.Yes : AnsiSupport.No,
            Out = new AnsiConsoleOutput(Console.Error)
        });

        // Failure text usually carries a command to copy; hard wrapping would split it across lines, so leave wrapping to the terminal.
        console.Profile.Width = ErrorConsoleWidth;
        return console;
    }
}
