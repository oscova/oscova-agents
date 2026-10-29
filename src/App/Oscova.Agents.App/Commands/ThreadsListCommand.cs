using System.Net.Http.Json;
using Oscova.Agents.App.Cli;
using Oscova.Agents.Conversations;
using Oscova.Agents.Host.Constants;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Oscova.Agents.App.Commands;

/// <summary>
/// Lists persisted chat threads through the host API without exposing internal thread fields.
/// </summary>
public sealed class ThreadsListCommand(CliHostResolver resolver, HostApiClient apiClient) : AsyncCommand<OscovaCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, OscovaCommandSettings settings, CancellationToken cancellationToken)
    {
        CliOutput.Configure(settings);
        var host = await resolver.ResolveAsync(settings, requireToken: true, cancellationToken).ConfigureAwait(false);
        using var client = apiClient.Create(host);
        var threads = await client.GetFromJsonAsync<List<ChatThread>>(ApiRoutes.Chat.Threads, CliJson.Options, cancellationToken).ConfigureAwait(false) ?? [];
        var rows = threads.Select(thread => new ThreadListRow(
            thread.Id,
            thread.Title,
            thread.AgentId,
            thread.AgentName,
            thread.UpdatedAt));

        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(rows, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        if (threads.Count == 0)
        {
            AnsiConsole.Write(CliRender.Card(
                "Threads (0)",
                new Text("No chat threads are available.", CliTheme.Label),
                CliTheme.AccentDim));
        }
        else
        {
            var table = CliRender.Table("Id", "Title", "Agent", "Updated");
            foreach (var row in rows)
            {
                table.AddRow(
                    CliRender.Identifier(row.Id),
                    CliRender.Value(string.IsNullOrWhiteSpace(row.Title) ? "(untitled)" : row.Title),
                    CliRender.Value(row.AgentName ?? row.AgentId ?? "(unknown)"),
                    CliRender.Value(row.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")));
            }

            CliRender.Separator($"Threads ({threads.Count})");
            AnsiConsole.Write(table);
        }

        CliOutput.Info("resume with: oscova ask --thread <id>", settings.Quiet);
        return 0;
    }

    private sealed record ThreadListRow(
        string Id,
        string Title,
        string? AgentId,
        string? AgentName,
        DateTimeOffset UpdatedAt);
}
