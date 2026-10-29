using System.ComponentModel;
using System.Net.Http.Json;
using Oscova.Agents.App.Cli;
using Oscova.Agents.Core.Logging;
using Oscova.Agents.Host.Constants;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Oscova.Agents.App.Commands;

// Log routes are scoped; the CLI always reads the signed-in user's own logs, because the
// system-wide and other-user scopes are an admin view that belongs in the web UI.
internal static class LogScope
{
    public const string Self = "self";
}

/// <summary>
/// Lists the log files available to the signed-in user.
/// </summary>
public sealed class LogsFilesCommand(CliHostResolver resolver, HostApiClient apiClient) : HostCommandBase<OscovaCommandSettings>(resolver, apiClient)
{
    protected override async Task<int> RunAsync(HttpClient client, OscovaCommandSettings settings, CancellationToken cancellationToken)
    {
        var files = await client.GetFromJsonAsync<List<string>>(ApiRoutes.Logs.Files(LogScope.Self, targetUserId: null), CliJson.Options, cancellationToken).ConfigureAwait(false) ?? [];

        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(files, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        if (files.Count == 0)
        {
            AnsiConsole.Write(CliRender.Card(
                "Log files (0)",
                new Text("No log files are available.", CliTheme.Label),
                CliTheme.AccentDim));
        }
        else
        {
            var table = CliRender.Table("File");
            foreach (var file in files)
            {
                table.AddRow(CliRender.Value(file));
            }

            CliRender.Separator($"Log files ({files.Count})");
            AnsiConsole.Write(table);
        }

        CliOutput.Info("read with: oscova logs read --file <name>", settings.Quiet);
        return 0;
    }
}

public sealed class LogReadSettings : OscovaCommandSettings
{
    [CommandOption("--file <NAME>")]
    [Description("Log file to read. List valid names with 'oscova logs files'. Defaults to the newest file.")]
    public string? File { get; init; }

    [CommandOption("--level <LEVEL>")]
    [Description("Only show entries at this level, for example Error or Warning.")]
    public string? Level { get; init; }

    [CommandOption("--search <TERM>")]
    [Description("Only show entries whose message, metadata, or exception contains this text.")]
    public string? Search { get; init; }

    [CommandOption("--page <NUMBER>")]
    [Description("Page of entries to read. Defaults to 1.")]
    public int? Page { get; init; }

    [CommandOption("--page-size <COUNT>")]
    [Description("Entries per page. Defaults to 100, clamped by the host to 1-500.")]
    public int? PageSize { get; init; }
}

/// <summary>
/// Reads one page of host log entries.
/// </summary>
public sealed class LogsReadCommand(CliHostResolver resolver, HostApiClient apiClient) : HostCommandBase<LogReadSettings>(resolver, apiClient)
{
    protected override async Task<int> RunAsync(HttpClient client, LogReadSettings settings, CancellationToken cancellationToken)
    {
        var route = ApiRoutes.Logs.Page(
            settings.File,
            settings.Level,
            settings.Search,
            settings.Page ?? 1,
            settings.PageSize ?? 100,
            LogScope.Self,
            targetUserId: null);

        using var response = await client.GetAsync(route, cancellationToken).ConfigureAwait(false);
        await CliHttpErrors.EnsureSuccessAsync(response, "Reading logs", cancellationToken).ConfigureAwait(false);
        var page = await response.Content.ReadFromJsonAsync<LogPage>(CliJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new CliExitException(1, "The host returned an empty log page.");

        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(page, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var query = new List<(string Key, string Value)>
        {
            ("File", page.FileName),
            ("Page", $"{page.CurrentPage} of {page.TotalPages}")
        };
        if (!string.IsNullOrWhiteSpace(settings.Level)) query.Add(("Level", settings.Level));
        if (!string.IsNullOrWhiteSpace(settings.Search)) query.Add(("Search", settings.Search));
        CliRender.KeyValue("Log query", query);

        if (page.Entries.Count == 0)
        {
            AnsiConsole.Write(CliRender.Card(
                "Log entries (0)",
                new Text("No log entries matched the current filters.", CliTheme.Label),
                CliTheme.AccentDim));
        }
        else
        {
            var table = CliRender.Table("Time", "Level", "Logger", "Message");
            foreach (var entry in page.Entries)
            {
                table.AddRow(
                    CliRender.Value(entry.Time),
                    new Text(entry.Level, LevelStyle(entry.Level)),
                    CliRender.Identifier(entry.ShortLogger),
                    CliRender.Value(entry.HasException ? $"{entry.Message} ({entry.Exception!.Type})" : entry.Message));
            }

            CliRender.Separator($"Log entries ({page.Entries.Count})");
            AnsiConsole.Write(table);
        }

        CliOutput.Info($"{page.FileName}: page {page.CurrentPage} of {page.TotalPages}, {page.TotalCount} entries.", settings.Quiet);
        return 0;
    }

    private static Style LevelStyle(string level) => level.ToLowerInvariant() switch
    {
        "error" or "fatal" or "critical" => new Style(CliTheme.Danger),
        "warning" or "warn" => new Style(CliTheme.Warning),
        "debug" or "trace" or "verbose" => CliTheme.Label,
        _ => CliTheme.Value
    };
}

public sealed class LogClearSettings : OscovaCommandSettings
{
    [CommandOption("--file <NAME>")]
    [Description("Log file to clear. Required. List valid names with 'oscova logs files'.")]
    public string? File { get; init; }

    [CommandOption("--yes")]
    [Description("Clear without asking for confirmation.")]
    public bool Yes { get; init; }
}

/// <summary>
/// Clears one log file. There is no clear-all; the host deletes exactly the named file.
/// </summary>
public sealed class LogsClearCommand(CliHostResolver resolver, HostApiClient apiClient) : HostCommandBase<LogClearSettings>(resolver, apiClient)
{
    protected override async Task<int> RunAsync(HttpClient client, LogClearSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.File))
        {
            throw new CliExitException(4, "--file is required. List log files with 'oscova logs files'.");
        }

        CliRender.ConfirmDestructive(settings, settings.Yes, $"Log file '{settings.File}' will be emptied.");

        using var response = await CliRender.StatusAsync(
            $"Clearing {settings.File}...", settings,
            () => client.DeleteAsync(
                ApiRoutes.Logs.Clear(settings.File, LogScope.Self, targetUserId: null),
                cancellationToken)).ConfigureAwait(false);
        await CliHttpErrors.EnsureSuccessAsync(response, "Clearing log file", cancellationToken).ConfigureAwait(false);
        var message = $"Cleared {settings.File}.";
        // Requested JSON results remain available when informational output is quiet.
        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(new { success = true, message }, cancellationToken).ConfigureAwait(false);
        }
        else if (!settings.Quiet &&
            AnsiConsole.Profile.Capabilities.Interactive && AnsiConsole.Profile.Capabilities.Ansi)
        {
            AnsiConsole.Write(CliRender.Card("Log cleared", new Text(message, CliTheme.Value), CliTheme.Success));
        }
        else
        {
            CliRender.Success(message);
        }

        return 0;
    }
}
