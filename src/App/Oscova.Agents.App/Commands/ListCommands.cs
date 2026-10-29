using System.Net.Http.Json;
using Oscova.Agents.App.Cli;
using Oscova.Agents.Core.ApiContracts.Agents;
using Oscova.Agents.Core.ApiContracts.ModelProviders;
using Oscova.Agents.Core.ApiContracts.Plugins;
using Oscova.Agents.Core.ApiContracts.Tasks;
using Oscova.Agents.Host.Constants;
using Oscova.Agents.Tasks;
using Spectre.Console;

namespace Oscova.Agents.App.Commands;

public abstract class ListCommandBase<TItem>(CliHostResolver resolver, HostApiClient apiClient) : HostCommandBase<OscovaCommandSettings>(resolver, apiClient)
{
    protected abstract string Endpoint { get; }
    protected abstract string CollectionName { get; }
    protected abstract string EmptyMessage { get; }
    protected abstract string[] Columns { get; }
    protected abstract void AddRow(Table table, TItem item);

    /// <summary>
    /// Fetches the rows to render; override when the endpoint returns a document rather than a list.
    /// </summary>
    protected virtual async Task<IReadOnlyList<TItem>> FetchAsync(HttpClient client, CancellationToken cancellationToken) =>
        await client.GetFromJsonAsync<List<TItem>>(Endpoint, CliJson.Options, cancellationToken).ConfigureAwait(false) ?? [];

    /// <summary>
    /// Writes an optional hint under the rendered table.
    /// </summary>
    protected virtual void AfterTable()
    {
    }

    protected override async Task<int> RunAsync(HttpClient client, OscovaCommandSettings settings, CancellationToken cancellationToken)
    {
        var items = await FetchAsync(client, cancellationToken).ConfigureAwait(false);

        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(items, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        if (items.Count == 0)
        {
            AnsiConsole.Write(CliRender.Card(
                $"{CollectionName} (0)",
                new Text(EmptyMessage, CliTheme.Label),
                CliTheme.AccentDim));
        }
        else
        {
            var table = CliRender.Table(Columns);
            foreach (var item in items)
            {
                AddRow(table, item);
            }

            CliRender.Separator($"{CollectionName} ({items.Count})");
            AnsiConsole.Write(table);
        }

        AfterTable();
        return 0;
    }
}

public sealed class AgentsListCommand(CliHostResolver resolver, HostApiClient apiClient) : ListCommandBase<AgentListRow>(resolver, apiClient)
{
    protected override string Endpoint => ApiRoutes.Agents.Configuration;
    protected override string CollectionName => "Agents";
    protected override string EmptyMessage => "No agents are available.";

    protected override string[] Columns => ["ID", "Name", "Model", "Source"];

    protected override async Task<IReadOnlyList<AgentListRow>> FetchAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var configuration = await client.GetFromJsonAsync<AgentConfiguration>(Endpoint, CliJson.Options, cancellationToken).ConfigureAwait(false) ?? new AgentConfiguration();
        var modelNames = configuration.AvailableModelProviders.ToDictionary(provider => provider.Id, provider => provider.Name, StringComparer.OrdinalIgnoreCase);
        var agents = new List<AgentListRow>
        {
            new(configuration.MainAgent.Id, configuration.MainAgent.Name, ResolveModelName(configuration.MainAgent.ModelOverrideId, modelNames), "main")
        };

        agents.AddRange(configuration.CustomAgents.Select(agent => new AgentListRow(
            agent.Id,
            agent.Name,
            ResolveModelName(agent.ModelOverrideId, modelNames),
            agent.IsShared ? "shared" : "custom")));

        return agents;
    }

    protected override void AddRow(Table table, AgentListRow item) => table.AddRow(
        CliRender.Identifier(item.Id),
        CliRender.Value(item.Name),
        CliRender.Value(item.Model),
        CliRender.Value(item.Source));

    private static string ResolveModelName(string? modelProviderId, IReadOnlyDictionary<string, string> modelNames)
    {
        if (string.IsNullOrWhiteSpace(modelProviderId))
        {
            return "(user default)";
        }

        return modelNames.TryGetValue(modelProviderId, out var name) ? name : "(unavailable)";
    }
}

public sealed class ModelsListCommand(CliHostResolver resolver, HostApiClient apiClient) : ListCommandBase<SelectableModelProvider>(resolver, apiClient)
{
    protected override string Endpoint => ApiRoutes.Agents.Configuration;
    protected override string CollectionName => "Models";
    protected override string EmptyMessage => "No model providers are available.";

    protected override string[] Columns => ["ID", "Name", "Model"];

    protected override async Task<IReadOnlyList<SelectableModelProvider>> FetchAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var configuration = await client.GetFromJsonAsync<AgentConfiguration>(Endpoint, CliJson.Options, cancellationToken).ConfigureAwait(false) ?? new AgentConfiguration();
        return [.. configuration.AvailableModelProviders];
    }

    protected override void AddRow(Table table, SelectableModelProvider item) => table.AddRow(
        CliRender.Identifier(item.Id),
        CliRender.Value(item.Name),
        CliRender.Value(item.ModelId ?? "(default)"));
}

public sealed class TasksListCommand(CliHostResolver resolver, HostApiClient apiClient) : ListCommandBase<AgentTaskDefinition>(resolver, apiClient)
{
    protected override string Endpoint => ApiRoutes.Tasks.Base;
    protected override string CollectionName => "Tasks";
    protected override string EmptyMessage => "No tasks are configured.";

    protected override string[] Columns => ["ID", "Agent", "Title", "Enabled"];

    protected override async Task<IReadOnlyList<AgentTaskDefinition>> FetchAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var configuration = await client.GetFromJsonAsync<TaskConfiguration>(Endpoint, CliJson.Options, cancellationToken).ConfigureAwait(false) ?? new TaskConfiguration();
        return [.. configuration.Tasks];
    }

    protected override void AddRow(Table table, AgentTaskDefinition item) => table.AddRow(
        CliRender.Identifier(item.Id),
        CliRender.Value(item.AgentId),
        CliRender.Value(item.Name),
        CliRender.State(item.IsEnabled ? "yes" : "no", item.IsEnabled));
}

public sealed class PluginsListCommand(CliHostResolver resolver, HostApiClient apiClient) : ListCommandBase<PluginSettingsEntry>(resolver, apiClient)
{
    protected override string Endpoint => ApiRoutes.PluginSettings.Configuration;
    protected override string CollectionName => "Plugins";
    protected override string EmptyMessage => "No plugins are installed.";

    protected override string[] Columns => ["ID", "Name", "Status", "Load next restart"];

    protected override async Task<IReadOnlyList<PluginSettingsEntry>> FetchAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var summary = await client.GetFromJsonAsync<PluginSettingsSummary>(Endpoint, CliJson.Options, cancellationToken).ConfigureAwait(false) ?? new PluginSettingsSummary();
        return [.. summary.Plugins];
    }

    protected override void AddRow(Table table, PluginSettingsEntry item) => table.AddRow(
        CliRender.Identifier(item.Id ?? string.Empty),
        CliRender.Value(item.Name ?? string.Empty),
        CliRender.Status(item.Status.ToString()),
        CliRender.State(item.LoadOnNextRestart ? "yes" : "no", item.LoadOnNextRestart));
}

public sealed class SkillsListCommand(CliHostResolver resolver, HostApiClient apiClient) : ListCommandBase<SkillDefinition>(resolver, apiClient)
{
    protected override string Endpoint => ApiRoutes.Agents.Configuration;
    protected override string CollectionName => "Skills";
    protected override string EmptyMessage => "No skills are available.";

    protected override string[] Columns => ["Name", "Description"];

    protected override async Task<IReadOnlyList<SkillDefinition>> FetchAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var configuration = await client.GetFromJsonAsync<AgentConfiguration>(Endpoint, CliJson.Options, cancellationToken).ConfigureAwait(false) ?? new AgentConfiguration();
        return [.. configuration.AvailableSkills];
    }

    protected override void AddRow(Table table, SkillDefinition item) => table.AddRow(
        CliRender.Value(item.Name),
        CliRender.Value(item.Description));
}

public sealed record AgentListRow(string Id, string Name, string Model, string Source);
