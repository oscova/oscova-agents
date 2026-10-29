using System.ComponentModel;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.AI;
using Oscova.Agents.App.Cli;
using Oscova.Agents.Conversations;
using Oscova.Agents.Core.ApiContracts.Chat;
using Oscova.Agents.Host.Constants;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Oscova.Agents.App.Commands;

// There is deliberately no 'threads stop' command. POST api/chat/threads/{id}/stop rejects a request
// whose body carries no RunId, and a chat run id is never surfaced to the CLI, so the command could
// only ever fail. Stopping a run needs either a --run option fed by run ids in 'threads show' or a
// host change that stops the thread's active run when RunId is absent.

public class ThreadIdSettings : OscovaCommandSettings
{
    [CommandArgument(0, "<THREAD_ID>")]
    [Description("Thread identifier. List valid ids with 'oscova threads list'.")]
    public string ThreadId { get; init; } = string.Empty;
}

/// <summary>
/// Shows one thread's metadata.
/// </summary>
public sealed class ThreadsShowCommand(CliHostResolver resolver, HostApiClient apiClient) : HostCommandBase<ThreadIdSettings>(resolver, apiClient)
{
    protected override async Task<int> RunAsync(HttpClient client, ThreadIdSettings settings, CancellationToken cancellationToken)
    {
        var thread = await ThreadCommandSupport.GetThreadAsync(client, settings.ThreadId, cancellationToken).ConfigureAwait(false);
        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(thread, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        CliRender.KeyValue(string.IsNullOrWhiteSpace(thread.Title) ? "(untitled)" : thread.Title,
        [
            ("Id", thread.Id),
            ("Agent", thread.AgentName ?? thread.AgentId ?? "(unknown)"),
            ("Updated", thread.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")),
            ("Messages", thread.DisplayMessages.Count.ToString())
        ]);

        return 0;
    }
}

/// <summary>
/// Prints the session messages of one thread.
/// </summary>
public sealed class ThreadsMessagesCommand(CliHostResolver resolver, HostApiClient apiClient) : HostCommandBase<ThreadIdSettings>(resolver, apiClient)
{
    protected override async Task<int> RunAsync(HttpClient client, ThreadIdSettings settings, CancellationToken cancellationToken)
    {
        var route = ApiRoutes.Chat.SessionMessages(settings.ThreadId);
        using var response = await client.GetAsync(route, cancellationToken).ConfigureAwait(false);
        ThreadCommandSupport.ThrowIfMissingThread(response, settings.ThreadId);
        await CliHttpErrors.EnsureSuccessAsync(response, "Reading thread messages", cancellationToken).ConfigureAwait(false);
        var messages = await response.Content.ReadFromJsonAsync<List<ChatMessage>>(CliJson.Options, cancellationToken).ConfigureAwait(false) ?? [];

        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(messages, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var renderedAny = false;
        foreach (var message in messages)
        {
            var text = message.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            renderedAny = true;
            CliRender.Separator(message.Role.Value);
            AnsiConsole.Write(CliMarkdown.Render(text));
        }

        if (!renderedAny)
        {
            AnsiConsole.Write(CliRender.Card(
                "Thread messages (0)",
                new Text("No messages are available for this thread.", CliTheme.Label),
                CliTheme.AccentDim));
        }

        return 0;
    }
}

public sealed class ThreadNewSettings : OscovaCommandSettings
{
    [CommandOption("-a|--agent <AGENT_ID>")]
    [Description("Agent to assign to the new thread. List valid ids with 'oscova agents list'.")]
    public string? AgentId { get; init; }
}

/// <summary>
/// Creates an empty thread and prints its identifier.
/// </summary>
public sealed class ThreadsNewCommand(CliHostResolver resolver, HostApiClient apiClient) : HostCommandBase<ThreadNewSettings>(resolver, apiClient)
{
    protected override async Task<int> RunAsync(HttpClient client, ThreadNewSettings settings, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(
            ApiRoutes.Chat.Threads,
            new CreateThreadRequest { AgentId = settings.AgentId },
            CliJson.Options,
            cancellationToken).ConfigureAwait(false);
        await CliHttpErrors.EnsureSuccessAsync(response, "Creating thread", cancellationToken).ConfigureAwait(false);
        var thread = await response.Content.ReadFromJsonAsync<ChatThread>(CliJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new CliExitException(1, "The host returned an empty thread.");

        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(thread, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var message = $"Created thread {thread.Id}. Use it with 'oscova ask --thread {thread.Id}'.";
        if (!settings.Quiet && AnsiConsole.Profile.Capabilities.Interactive && AnsiConsole.Profile.Capabilities.Ansi)
        {
            CliRender.KeyValue("Thread created",
            [
                ("ID", CliRender.Identifier(thread.Id)),
                ("Continue", CliRender.Value($"oscova ask --thread {thread.Id}"))
            ]);
        }
        else
        {
            CliRender.Success(message);
        }

        return 0;
    }
}

public sealed class ThreadRenameSettings : ThreadIdSettings
{
    [CommandArgument(1, "<TITLE>")]
    [Description("New title for the thread.")]
    public string Title { get; init; } = string.Empty;
}

/// <summary>
/// Renames a thread, preserving every other field on it.
/// </summary>
public sealed class ThreadsRenameCommand(CliHostResolver resolver, HostApiClient apiClient) : HostCommandBase<ThreadRenameSettings>(resolver, apiClient)
{
    protected override async Task<int> RunAsync(HttpClient client, ThreadRenameSettings settings, CancellationToken cancellationToken)
    {
        // Use the title-only boundary so renaming cannot alter current, unread, or transcript state.
        using var response = await client.PutAsJsonAsync(
            ApiRoutes.Chat.ThreadTitle(settings.ThreadId),
            new RenameThreadRequest { Title = settings.Title },
            CliJson.Options,
            cancellationToken).ConfigureAwait(false);
        ThreadCommandSupport.ThrowIfMissingThread(response, settings.ThreadId);
        await CliHttpErrors.EnsureSuccessAsync(response, "Renaming thread", cancellationToken).ConfigureAwait(false);
        var message = $"Renamed thread {settings.ThreadId}.";
        // Requested JSON results remain available when informational output is quiet.
        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(new { success = true, message }, cancellationToken).ConfigureAwait(false);
        }
        else if (!settings.Quiet &&
            AnsiConsole.Profile.Capabilities.Interactive && AnsiConsole.Profile.Capabilities.Ansi)
        {
            CliRender.KeyValue("Thread renamed",
            [
                ("ID", CliRender.Identifier(settings.ThreadId)),
                ("Title", CliRender.Value(settings.Title))
            ]);
        }
        else
        {
            CliRender.Success(message);
        }

        return 0;
    }
}

public sealed class ThreadDeleteSettings : ThreadIdSettings
{
    [CommandOption("--yes")]
    [Description("Delete without asking for confirmation.")]
    public bool Yes { get; init; }
}

/// <summary>
/// Deletes one thread and its history.
/// </summary>
public sealed class ThreadsDeleteCommand(CliHostResolver resolver, HostApiClient apiClient) : HostCommandBase<ThreadDeleteSettings>(resolver, apiClient)
{
    protected override async Task<int> RunAsync(HttpClient client, ThreadDeleteSettings settings, CancellationToken cancellationToken)
    {
        CliRender.ConfirmDestructive(settings, settings.Yes, $"Thread '{settings.ThreadId}' and its history will be deleted.");

        using var response = await CliRender.StatusAsync(
            $"Deleting thread {settings.ThreadId}...", settings,
            () => client.DeleteAsync(ApiRoutes.Chat.DeleteThread(settings.ThreadId), cancellationToken)).ConfigureAwait(false);
        ThreadCommandSupport.ThrowIfMissingThread(response, settings.ThreadId);
        await CliHttpErrors.EnsureSuccessAsync(response, "Deleting thread", cancellationToken).ConfigureAwait(false);
        var message = $"Deleted thread {settings.ThreadId}.";
        // Requested JSON results remain available when informational output is quiet.
        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(new { success = true, message }, cancellationToken).ConfigureAwait(false);
        }
        else if (!settings.Quiet &&
            AnsiConsole.Profile.Capabilities.Interactive && AnsiConsole.Profile.Capabilities.Ansi)
        {
            AnsiConsole.Write(CliRender.Card("Thread deleted", new Text(message, CliTheme.Value), CliTheme.Success));
        }
        else
        {
            CliRender.Success(message);
        }

        return 0;
    }
}

public sealed class ThreadClearSettings : OscovaCommandSettings
{
    [CommandOption("--yes")]
    [Description("Delete without asking for confirmation.")]
    public bool Yes { get; init; }
}

/// <summary>
/// Deletes every thread belonging to the signed-in user.
/// </summary>
public sealed class ThreadsClearCommand(CliHostResolver resolver, HostApiClient apiClient) : HostCommandBase<ThreadClearSettings>(resolver, apiClient)
{
    protected override async Task<int> RunAsync(HttpClient client, ThreadClearSettings settings, CancellationToken cancellationToken)
    {
        CliRender.ConfirmDestructive(settings, settings.Yes, "Every chat thread for this account will be deleted.");

        using var response = await CliRender.StatusAsync(
            "Deleting every thread...", settings,
            () => client.DeleteAsync(ApiRoutes.Chat.Threads, cancellationToken)).ConfigureAwait(false);
        await CliHttpErrors.EnsureSuccessAsync(response, "Deleting threads", cancellationToken).ConfigureAwait(false);
        const string message = "Deleted every thread.";
        // Requested JSON results remain available when informational output is quiet.
        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(new { success = true, message }, cancellationToken).ConfigureAwait(false);
        }
        else if (!settings.Quiet &&
            AnsiConsole.Profile.Capabilities.Interactive && AnsiConsole.Profile.Capabilities.Ansi)
        {
            AnsiConsole.Write(CliRender.Card("Threads deleted", new Text(message, CliTheme.Value), CliTheme.Success));
        }
        else
        {
            CliRender.Success(message);
        }

        return 0;
    }
}

internal static class ThreadCommandSupport
{
    /// <summary>
    /// Turns a missing-thread response into a usage failure rather than a generic error.
    /// </summary>
    public static void ThrowIfMissingThread(HttpResponseMessage response, string threadId)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new CliExitException(4, $"No thread '{threadId}'. List threads with 'oscova threads list'.");
        }
    }

    public static async Task<ChatThread> GetThreadAsync(HttpClient client, string threadId, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(ApiRoutes.Chat.Thread(threadId), cancellationToken).ConfigureAwait(false);
        ThrowIfMissingThread(response, threadId);
        await CliHttpErrors.EnsureSuccessAsync(response, "Reading thread", cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ChatThread>(CliJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new CliExitException(1, "The host returned an empty thread.");
    }
}
