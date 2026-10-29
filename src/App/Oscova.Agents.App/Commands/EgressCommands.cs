using System.ComponentModel;
using System.Globalization;
using System.Net.Http.Json;
using Oscova.Agents.App.Cli;
using Oscova.Agents.Core.ApiContracts.Audit;
using Oscova.Agents.Core.ApiContracts.Common;
using Oscova.Agents.Host.Constants;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Oscova.Agents.App.Commands;

public sealed class EgressListSettings : OscovaCommandSettings
{
    [CommandOption("--page <NUMBER>")]
    [Description("Page of entries to read. Defaults to 1.")]
    public int? Page { get; init; }

    [CommandOption("--page-size <COUNT>")]
    [Description("Entries per page. Defaults to 50, clamped by the host to 100.")]
    public int? PageSize { get; init; }

    [CommandOption("--from <TIMESTAMP>")]
    [Description("Only show entries at or after this time, for example 2026-08-01 or 2026-08-01T09:00:00Z.")]
    public string? From { get; init; }

    [CommandOption("--to <TIMESTAMP>")]
    [Description("Only show entries at or before this time.")]
    public string? To { get; init; }

    [CommandOption("--kind <KIND>")]
    [Description("Only show entries of this kind: provider, tool, or embedding.")]
    public string? Kind { get; init; }

    [CommandOption("--source <SOURCE>")]
    [Description("Only show entries from this source, for example chat or task.")]
    public string? Source { get; init; }

    [CommandOption("--search <TERM>")]
    [Description("Only show entries containing this text.")]
    public string? Search { get; init; }
}

/// <summary>
/// Reads one page of the egress audit ledger, which records every time data left this machine.
/// </summary>
public sealed class EgressListCommand(CliHostResolver resolver, HostApiClient apiClient) : HostCommandBase<EgressListSettings>(resolver, apiClient)
{
    // The CLI reads only the signed-in user's own ledger; the system-wide scope is an admin view in the web UI.
    private const string SelfScope = "self";

    protected override async Task<int> RunAsync(HttpClient client, EgressListSettings settings, CancellationToken cancellationToken)
    {
        var route = ApiRoutes.EgressAudit.Query(
            SelfScope,
            settings.Page ?? 1,
            settings.PageSize ?? 50,
            ParseTimestamp(settings.From, "--from"),
            ParseTimestamp(settings.To, "--to"),
            settings.Kind,
            settings.Source,
            settings.Search);

        using var response = await client.GetAsync(route, cancellationToken).ConfigureAwait(false);
        await CliHttpErrors.EnsureSuccessAsync(response, "Reading the egress ledger", cancellationToken).ConfigureAwait(false);
        var page = await response.Content.ReadFromJsonAsync<EgressAuditPage>(CliJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new CliExitException(1, "The host returned an empty egress page.");

        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(page, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var query = new List<(string Key, string Value)>
        {
            ("Page", $"{page.CurrentPage} of {page.TotalPages}"),
            ("Page size", page.PageSize.ToString(CultureInfo.InvariantCulture))
        };
        if (!string.IsNullOrWhiteSpace(settings.From)) query.Add(("From", settings.From));
        if (!string.IsNullOrWhiteSpace(settings.To)) query.Add(("To", settings.To));
        if (!string.IsNullOrWhiteSpace(settings.Kind)) query.Add(("Kind", settings.Kind));
        if (!string.IsNullOrWhiteSpace(settings.Source)) query.Add(("Source", settings.Source));
        if (!string.IsNullOrWhiteSpace(settings.Search)) query.Add(("Search", settings.Search));

        CliRender.KeyValue("Egress query", query);
        if (page.Entries.Count == 0)
        {
            AnsiConsole.Write(CliRender.Card(
                "Egress audit (0)",
                new Text("No egress entries matched the current filters.", CliTheme.Label),
                CliTheme.AccentDim));
        }
        else
        {
            var table = CliRender.Table("Time", "Kind", "Source", "Destination", "Context");
            foreach (var entry in page.Entries)
            {
                var record = entry.Record;
                table.AddRow(
                    CliRender.Value(record.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
                    CliRender.Status(record.Kind),
                    CliRender.Value(record.Source),
                    CliRender.Value(Destination(entry)),
                    CliRender.Value(Context(entry)));
            }

            CliRender.Separator($"Egress audit ({page.Entries.Count})");
            AnsiConsole.Write(table);
        }

        CliOutput.Info(
            $"page {page.CurrentPage} of {page.TotalPages}, {page.TotalCount} entries ({page.Summary.ProviderCount} provider, {page.Summary.ToolCount} tool, {page.Summary.EmbeddingCount} embedding).",
            settings.Quiet);
        return 0;
    }

    private static string Destination(EgressAuditEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.ToolLabel))
        {
            return Label(entry.ToolLabel);
        }

        var provider = entry.ProviderLabel is null ? entry.Record.ProviderId : Label(entry.ProviderLabel);
        return string.IsNullOrWhiteSpace(entry.Record.ModelId) ? provider ?? "-" : $"{provider} / {entry.Record.ModelId}";
    }

    private static string Context(EgressAuditEntry entry) =>
        entry.Record.ThreadId ?? entry.Record.TaskId ?? (entry.AgentLabel is null ? "-" : Label(entry.AgentLabel));

    // The CLI is English-only, so reserved labels map to their English text here.
    private static string Label(string label) => label switch
    {
        RecordLabelKeys.DeletedAgent => "Deleted agent",
        RecordLabelKeys.DeletedProvider => "Deleted provider",
        RecordLabelKeys.DeletedMcpServer => "Deleted MCP server",
        RecordLabelKeys.OwnDestination => "Own destination",
        RecordLabelKeys.OtherUsersAgents => "Other users' agents",
        RecordLabelKeys.UsersOwnProviders => "Users' own providers",
        RecordLabelKeys.UsersOwnModels => "Users' own models",
        RecordLabelKeys.UsersOwnTools => "Users' own tools",
        _ => label
    };

    private static DateTimeOffset? ParseTimestamp(string? value, string option)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : throw new CliExitException(4, $"'{value}' is not a timestamp {option} understands. Use a date such as 2026-08-01 or 2026-08-01T09:00:00Z.");
    }
}
