using System.Net.Http.Json;
using Oscova.Agents.App.Cli;
using Oscova.Agents.Core.ApiContracts.Tools;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Host.Constants;
using Oscova.Agents.Policies;
using Spectre.Console;

namespace Oscova.Agents.App.Commands;

/// <summary>
/// Lists every governed tool with its network scope and call budget.
/// </summary>
public sealed class ToolsListCommand(CliHostResolver resolver, HostApiClient apiClient) : HostCommandBase<OscovaCommandSettings>(resolver, apiClient)
{
    protected override async Task<int> RunAsync(HttpClient client, OscovaCommandSettings settings, CancellationToken cancellationToken)
    {
        var summary = await client.GetFromJsonAsync<ToolGovernanceSummary>(ApiRoutes.ToolGovernance.Summary, CliJson.Options, cancellationToken).ConfigureAwait(false)
            ?? new ToolGovernanceSummary();

        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(summary, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        if (summary.Tools.Count == 0)
        {
            AnsiConsole.Write(CliRender.Card(
                "Tool governance (0)",
                new Text("No governed tools are available.", CliTheme.Label),
                CliTheme.AccentDim));
        }
        else
        {
            var table = CliRender.Table("Tool", "Declared", "Effective", "Limit", "Used");
            foreach (var tool in summary.Tools)
            {
                var effective = tool.EffectiveNetworkScope.ToString();
                table.AddRow(
                    CliRender.Identifier(tool.QualifiedName),
                    CliRender.Value(tool.DeclaredNetworkScope.ToString()),
                    new Text(tool.HasNetworkScopeOverride ? $"{effective} *" : effective, ScopeStyle(tool.EffectiveNetworkScope)),
                    CliRender.Value(FormatLimit(tool.CallLimit)),
                    CliRender.Value(FormatUsage(tool.Usage)));
            }

            CliRender.Separator($"Tool governance ({summary.Tools.Count})");
            AnsiConsole.Write(table);
        }

        CliOutput.Info("* marks a tool whose network scope you overrode.", settings.Quiet);
        return 0;
    }

    private static Style ScopeStyle(NetworkScope scope) => scope switch
    {
        NetworkScope.None => new Style(CliTheme.Success),
        NetworkScope.Egress => new Style(CliTheme.Warning),
        _ => CliTheme.Value
    };

    private static string FormatLimit(ToolCallLimit? limit) =>
        limit is { IsEnabled: true } ? $"{limit.MaxCalls} / {limit.WindowHours}h" : "none";

    private static string FormatUsage(ToolQuotaUsageSummary? usage) =>
        usage is null ? "-" : $"{usage.Used} of {usage.MaxCalls}";
}
