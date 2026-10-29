using Spectre.Console.Cli;
using System.ComponentModel;

namespace Oscova.Agents.App.Cli;

public class OscovaCommandSettings : CommandSettings
{
    [CommandOption("-H|--host <URL>")]
    [Description("Oscova host URL for client commands.")]
    public string? Host { get; init; }

    [CommandOption("--quiet")]
    [Description("Suppress informational output. Errors and requested data still print.")]
    public bool Quiet { get; init; }

    [CommandOption("--no-color")]
    [Description("Disable colour and ANSI styling. Also honoured via the NO_COLOR environment variable.")]
    public bool NoColor { get; init; }

    [CommandOption("--no-interactive")]
    [Description("Never prompt. Commands that would need input fail with exit code 4 instead.")]
    public bool NoInteractive { get; init; }

    [CommandOption("--json")]
    [Description("Emit machine-readable JSON on stdout instead of formatted output.")]
    public bool Json { get; init; }
}