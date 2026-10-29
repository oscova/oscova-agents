using System.ComponentModel;
using System.Net;
using System.Net.Http.Json;
using Oscova.Agents.App.Cli;
using Oscova.Agents.Core.ApiContracts.Tasks;
using Oscova.Agents.Host.Constants;
using Oscova.Agents.Tasks;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Oscova.Agents.App.Commands;

public class TaskIdSettings : OscovaCommandSettings
{
    [CommandArgument(0, "<TASK_ID>")]
    [Description("Task identifier. List valid ids with 'oscova tasks list'.")]
    public string TaskId { get; init; } = string.Empty;
}

/// <summary>
/// Shows one task definition.
/// </summary>
public sealed class TasksShowCommand(CliHostResolver resolver, HostApiClient apiClient) : HostCommandBase<TaskIdSettings>(resolver, apiClient)
{
    protected override async Task<int> RunAsync(HttpClient client, TaskIdSettings settings, CancellationToken cancellationToken)
    {
        var task = await TaskCommandSupport.GetTaskAsync(client, settings.TaskId, cancellationToken).ConfigureAwait(false);
        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(task, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        CliRender.KeyValue(task.Name,
        [
            ("Id", CliRender.Identifier(task.Id)),
            ("Agent", CliRender.Value(task.AgentId)),
            ("Enabled", CliRender.State(task.IsEnabled ? "yes" : "no", task.IsEnabled)),
            ("Session mode", CliRender.Value(task.SessionMode.ToString())),
            ("Notify on completion", CliRender.State(task.NotifyOnCompletion ? "yes" : "no", task.NotifyOnCompletion)),
            ("Model override", CliRender.Value(task.ModelOverrideId ?? "(user default)")),
            ("Created", CliRender.Value(task.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")))
        ]);
        AnsiConsole.Write(CliRender.Card(
            "Instruction",
            new Text(task.Instruction, CliTheme.Value),
            CliTheme.AccentDim));

        return 0;
    }
}

public sealed class TaskRunsSettings : TaskIdSettings
{
    [CommandOption("--limit <COUNT>")]
    [Description("Number of runs to return, newest first. Defaults to 20, clamped by the host to 1-100.")]
    public int? Limit { get; init; }
}

/// <summary>
/// Lists the recorded runs of one task.
/// </summary>
public sealed class TasksRunsCommand(CliHostResolver resolver, HostApiClient apiClient) : HostCommandBase<TaskRunsSettings>(resolver, apiClient)
{
    protected override async Task<int> RunAsync(HttpClient client, TaskRunsSettings settings, CancellationToken cancellationToken)
    {
        var route = ApiRoutes.Tasks.Runs(settings.TaskId, settings.Limit);

        using var response = await client.GetAsync(route, cancellationToken).ConfigureAwait(false);
        TaskCommandSupport.ThrowIfMissingTask(response, settings.TaskId);
        await CliHttpErrors.EnsureSuccessAsync(response, "Reading task runs", cancellationToken).ConfigureAwait(false);
        var runs = await response.Content.ReadFromJsonAsync<List<AgentTaskRun>>(CliJson.Options, cancellationToken).ConfigureAwait(false) ?? [];

        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(runs, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        if (runs.Count == 0)
        {
            AnsiConsole.Write(CliRender.Card(
                $"Task {settings.TaskId} runs (0)",
                new Text("No runs are recorded for this task.", CliTheme.Label),
                CliTheme.AccentDim));
        }
        else
        {
            var table = CliRender.Table("Run", "Status", "Started", "Completed", "Summary");
            foreach (var run in runs)
            {
                table.AddRow(
                    CliRender.Identifier(run.Id),
                    CliRender.Status(run.Status.ToString()),
                    CliRender.Value(FormatTime(run.StartedAt)),
                    CliRender.Value(FormatTime(run.CompletedAt)),
                    CliRender.Value(run.Error ?? run.ResultSummary ?? string.Empty));
            }

            CliRender.Separator($"Task {settings.TaskId} runs ({runs.Count})");
            AnsiConsole.Write(table);
        }

        return 0;
    }

    private static string FormatTime(DateTimeOffset? value) =>
        value is null ? "-" : value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

public sealed class TaskRunShowSettings : TaskIdSettings
{
    [CommandArgument(1, "<RUN_ID>")]
    [Description("Run identifier. List valid ids with 'oscova tasks runs <TASK_ID>'.")]
    public string RunId { get; init; } = string.Empty;
}

/// <summary>
/// Shows one recorded task run.
/// </summary>
public sealed class TasksRunShowCommand(CliHostResolver resolver, HostApiClient apiClient) : HostCommandBase<TaskRunShowSettings>(resolver, apiClient)
{
    protected override async Task<int> RunAsync(HttpClient client, TaskRunShowSettings settings, CancellationToken cancellationToken)
    {
        var route = ApiRoutes.Tasks.Run(settings.TaskId, settings.RunId);
        using var response = await client.GetAsync(route, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new CliExitException(4, $"No run '{settings.RunId}' on task '{settings.TaskId}'.");
        }

        await CliHttpErrors.EnsureSuccessAsync(response, "Reading task run", cancellationToken).ConfigureAwait(false);
        var run = await response.Content.ReadFromJsonAsync<AgentTaskRun>(CliJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new CliExitException(1, "The host returned an empty run.");

        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(run, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        CliRender.KeyValue("Task run",
        [
            ("Run", CliRender.Identifier(run.Id)),
            ("Task", CliRender.Identifier(run.TaskId)),
            ("Status", CliRender.Status(run.Status.ToString())),
            ("Trigger", CliRender.Value(run.TriggerSource.ToString())),
            ("Started", CliRender.Value(run.StartedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-")),
            ("Completed", CliRender.Value(run.CompletedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-")),
            ("Tokens", CliRender.Value(run.TokensUsed?.ToString() ?? "-"))
        ]);

        if (!string.IsNullOrWhiteSpace(run.Error))
        {
            CliRender.Failure(run.Error);
        }

        if (!string.IsNullOrWhiteSpace(run.ResultSummary))
        {
            CliRender.Separator("Result");
            AnsiConsole.Write(CliMarkdown.Render(run.ResultSummary));
        }

        return 0;
    }
}

/// <summary>
/// Runs a task immediately instead of waiting for its schedule.
/// </summary>
public sealed class TasksTriggerCommand(CliHostResolver resolver, HostApiClient apiClient) : HostCommandBase<TaskIdSettings>(resolver, apiClient)
{
    protected override async Task<int> RunAsync(HttpClient client, TaskIdSettings settings, CancellationToken cancellationToken)
    {
        var route = ApiRoutes.Tasks.Trigger(settings.TaskId);
        var response = await CliRender.StatusAsync(
            "Triggering task...", settings,
            () => client.PostAsync(route, content: null, cancellationToken)).ConfigureAwait(false);

        using (response)
        {
            TaskCommandSupport.ThrowIfMissingTask(response, settings.TaskId);

            // A conflict carries a TriggerTaskResult explaining why the run did not start.
            if (response.StatusCode != HttpStatusCode.Conflict)
            {
                await CliHttpErrors.EnsureSuccessAsync(response, "Triggering task", cancellationToken).ConfigureAwait(false);
            }

            var result = await response.Content.ReadFromJsonAsync<TriggerTaskResult>(CliJson.Options, cancellationToken).ConfigureAwait(false)
                ?? throw new CliExitException(1, "The host returned an empty trigger response.");

            if (settings.Json)
            {
                await CliOutput.WriteJsonAsync(result, cancellationToken).ConfigureAwait(false);
                return result.Started ? 0 : 1;
            }

            if (!result.Started)
            {
                CliRender.Failure(string.IsNullOrWhiteSpace(result.Message) ? "The task did not start." : result.Message);
                return 1;
            }

            var message = $"Started run {result.RunId}. Follow it with 'oscova tasks run-show {settings.TaskId} {result.RunId}'.";
            if (!settings.Quiet && AnsiConsole.Profile.Capabilities.Interactive && AnsiConsole.Profile.Capabilities.Ansi)
            {
                AnsiConsole.Write(CliRender.Card("Task run started", new Text(message, CliTheme.Value), CliTheme.Success));
            }
            else
            {
                CliRender.Success(message);
            }

            return 0;
        }
    }
}

public sealed class TaskDeleteSettings : TaskIdSettings
{
    [CommandOption("--yes")]
    [Description("Delete without asking for confirmation.")]
    public bool Yes { get; init; }
}

/// <summary>
/// Deletes a task and its run history.
/// </summary>
public sealed class TasksDeleteCommand(CliHostResolver resolver, HostApiClient apiClient) : HostCommandBase<TaskDeleteSettings>(resolver, apiClient)
{
    protected override async Task<int> RunAsync(HttpClient client, TaskDeleteSettings settings, CancellationToken cancellationToken)
    {
        CliRender.ConfirmDestructive(settings, settings.Yes, $"Task '{settings.TaskId}' will be deleted.");

        using var response = await CliRender.StatusAsync(
            $"Deleting task {settings.TaskId}...", settings,
            () => client.DeleteAsync(ApiRoutes.Tasks.ById(settings.TaskId), cancellationToken)).ConfigureAwait(false);
        TaskCommandSupport.ThrowIfMissingTask(response, settings.TaskId);
        await CliHttpErrors.EnsureSuccessAsync(response, "Deleting task", cancellationToken).ConfigureAwait(false);
        var message = $"Deleted task {settings.TaskId}.";
        // Requested JSON results remain available when informational output is quiet.
        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(new { success = true, message }, cancellationToken).ConfigureAwait(false);
        }
        else if (!settings.Quiet &&
            AnsiConsole.Profile.Capabilities.Interactive && AnsiConsole.Profile.Capabilities.Ansi)
        {
            AnsiConsole.Write(CliRender.Card("Task deleted", new Text(message, CliTheme.Value), CliTheme.Success));
        }
        else
        {
            CliRender.Success(message);
        }

        return 0;
    }
}

internal static class TaskCommandSupport
{
    /// <summary>
    /// Turns a missing-task response into a usage failure rather than a generic error.
    /// </summary>
    public static void ThrowIfMissingTask(HttpResponseMessage response, string taskId)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new CliExitException(4, $"No task '{taskId}'. List tasks with 'oscova tasks list'.");
        }
    }

    public static async Task<AgentTaskDefinition> GetTaskAsync(HttpClient client, string taskId, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(ApiRoutes.Tasks.ById(taskId), cancellationToken).ConfigureAwait(false);
        ThrowIfMissingTask(response, taskId);
        await CliHttpErrors.EnsureSuccessAsync(response, "Reading task", cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<AgentTaskDefinition>(CliJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new CliExitException(1, "The host returned an empty task.");
    }
}
