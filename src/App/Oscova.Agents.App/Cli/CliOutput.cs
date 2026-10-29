using System.Text.Json;
using Spectre.Console;

namespace Oscova.Agents.App.Cli;

public static class CliOutput
{
    public static void Configure(bool noColor)
    {
        if (noColor || Console.IsOutputRedirected || Environment.GetEnvironmentVariable("NO_COLOR") is not null)
        {
            AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                // Color suppression must retain cursor control for interactive selection prompts.
                Ansi = Console.IsOutputRedirected ? AnsiSupport.No : AnsiSupport.Detect,
                ColorSystem = ColorSystemSupport.NoColors,
                Out = new AnsiConsoleOutput(Console.Out)
            });
        }
    }

    public static void Configure(OscovaCommandSettings settings) => Configure(settings.NoColor);

    public static void Info(string message, bool quiet = false)
    {
        if (!quiet)
        {
            AnsiConsole.MarkupLine(message.EscapeMarkup());
        }
    }

    public static void Error(string message) => CliRender.Failure(message);

    public static async Task WriteJsonAsync<T>(T data, CancellationToken cancellationToken)
    {
        await JsonSerializer.SerializeAsync(Console.OpenStandardOutput(), data, CliJson.Options, cancellationToken).ConfigureAwait(false);
        await Console.Out.WriteLineAsync().ConfigureAwait(false);
    }
}

public static class CliJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}
