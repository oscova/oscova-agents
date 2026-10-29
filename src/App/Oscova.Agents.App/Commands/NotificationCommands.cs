using System.ComponentModel;
using System.Net.Http.Json;
using Oscova.Agents.App.Cli;
using Oscova.Agents.Core.Notifications;
using Oscova.Agents.Host.Constants;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Oscova.Agents.App.Commands;

/// <summary>
/// Lists the notification inbox, newest first.
/// </summary>
public sealed class NotificationsListCommand(CliHostResolver resolver, HostApiClient apiClient) : HostCommandBase<OscovaCommandSettings>(resolver, apiClient)
{
    protected override async Task<int> RunAsync(HttpClient client, OscovaCommandSettings settings, CancellationToken cancellationToken)
    {
        var entries = await NotificationCommandSupport.ListAsync(client, cancellationToken).ConfigureAwait(false);

        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(entries, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        if (entries.Count == 0)
        {
            AnsiConsole.Write(CliRender.Card(
                "Notifications (0)",
                new Text("The notification inbox is empty.", CliTheme.Label),
                CliTheme.AccentDim));
        }
        else
        {
            var table = CliRender.Table("ID", "Created", "Kind", "System", "Read", "Title");
            foreach (var entry in entries)
            {
                table.AddRow(
                    CliRender.Identifier(entry.Id),
                    CliRender.Value(entry.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")),
                    CliRender.Status(entry.Kind.ToString()),
                    CliRender.State(entry.IsSystem ? "yes" : "no", entry.IsSystem),
                    CliRender.State(entry.IsRead ? "yes" : "no", entry.IsRead),
                    CliRender.Value(entry.Title));
            }

            CliRender.Separator($"Notifications ({entries.Count})");
            AnsiConsole.Write(table);
        }

        return 0;
    }
}

public sealed class NotificationReadSettings : OscovaCommandSettings
{
    [CommandArgument(0, "<ENTRY_ID>")]
    [Description("Notification identifier. List valid ids with 'oscova notifications list'.")]
    public string EntryId { get; init; } = string.Empty;
}

/// <summary>
/// Marks one notification read in whichever store it came from.
/// </summary>
public sealed class NotificationsReadCommand(CliHostResolver resolver, HostApiClient apiClient) : HostCommandBase<NotificationReadSettings>(resolver, apiClient)
{
    protected override async Task<int> RunAsync(HttpClient client, NotificationReadSettings settings, CancellationToken cancellationToken)
    {
        // The user and system inboxes are separate stores, so the entry decides which one gets written.
        var entries = await NotificationCommandSupport.ListAsync(client, cancellationToken).ConfigureAwait(false);
        var entry = entries.FirstOrDefault(candidate => string.Equals(candidate.Id, settings.EntryId, StringComparison.Ordinal))
            ?? throw new CliExitException(4, $"No notification '{settings.EntryId}'. List notifications with 'oscova notifications list'.");

        var route = ApiRoutes.Notifications.MarkRead(entry.Id, entry.IsSystem);
        using var response = await CliRender.StatusAsync(
            $"Marking notification {entry.Id} read...", settings,
            () => client.PostAsync(route, content: null, cancellationToken)).ConfigureAwait(false);
        await CliHttpErrors.EnsureSuccessAsync(response, "Marking notification read", cancellationToken).ConfigureAwait(false);
        var message = $"Marked {entry.Id} read.";
        // Requested JSON results remain available when informational output is quiet.
        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(new { success = true, message }, cancellationToken).ConfigureAwait(false);
        }
        else if (!settings.Quiet &&
            AnsiConsole.Profile.Capabilities.Interactive && AnsiConsole.Profile.Capabilities.Ansi)
        {
            AnsiConsole.Write(CliRender.Card("Notification updated", new Text(message, CliTheme.Value), CliTheme.Success));
        }
        else
        {
            CliRender.Success(message);
        }

        return 0;
    }
}

/// <summary>
/// Marks every notification read.
/// </summary>
public sealed class NotificationsReadAllCommand(CliHostResolver resolver, HostApiClient apiClient) : HostCommandBase<OscovaCommandSettings>(resolver, apiClient)
{
    protected override async Task<int> RunAsync(HttpClient client, OscovaCommandSettings settings, CancellationToken cancellationToken)
    {
        using var response = await CliRender.StatusAsync(
            "Marking every notification read...", settings,
            () => client.PostAsync(ApiRoutes.Notifications.MarkAllRead, content: null, cancellationToken)).ConfigureAwait(false);
        await CliHttpErrors.EnsureSuccessAsync(response, "Marking notifications read", cancellationToken).ConfigureAwait(false);
        const string message = "Marked every notification read.";
        // Requested JSON results remain available when informational output is quiet.
        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(new { success = true, message }, cancellationToken).ConfigureAwait(false);
        }
        else if (!settings.Quiet &&
            AnsiConsole.Profile.Capabilities.Interactive && AnsiConsole.Profile.Capabilities.Ansi)
        {
            AnsiConsole.Write(CliRender.Card("Notifications updated", new Text(message, CliTheme.Value), CliTheme.Success));
        }
        else
        {
            CliRender.Success(message);
        }

        return 0;
    }
}

public sealed class NotificationClearSettings : OscovaCommandSettings
{
    [CommandOption("--yes")]
    [Description("Clear without asking for confirmation.")]
    public bool Yes { get; init; }
}

/// <summary>
/// Empties the notification inbox.
/// </summary>
public sealed class NotificationsClearCommand(CliHostResolver resolver, HostApiClient apiClient) : HostCommandBase<NotificationClearSettings>(resolver, apiClient)
{
    protected override async Task<int> RunAsync(HttpClient client, NotificationClearSettings settings, CancellationToken cancellationToken)
    {
        CliRender.ConfirmDestructive(settings, settings.Yes, "Every notification in the inbox will be deleted.");

        using var response = await CliRender.StatusAsync(
            "Clearing notifications...", settings,
            () => client.PostAsync(ApiRoutes.Notifications.Clear, content: null, cancellationToken)).ConfigureAwait(false);
        await CliHttpErrors.EnsureSuccessAsync(response, "Clearing notifications", cancellationToken).ConfigureAwait(false);
        const string message = "Cleared the notification inbox.";
        // Requested JSON results remain available when informational output is quiet.
        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(new { success = true, message }, cancellationToken).ConfigureAwait(false);
        }
        else if (!settings.Quiet &&
            AnsiConsole.Profile.Capabilities.Interactive && AnsiConsole.Profile.Capabilities.Ansi)
        {
            AnsiConsole.Write(CliRender.Card("Notifications cleared", new Text(message, CliTheme.Value), CliTheme.Success));
        }
        else
        {
            CliRender.Success(message);
        }

        return 0;
    }
}

internal static class NotificationCommandSupport
{
    public static async Task<List<NotificationEntry>> ListAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(ApiRoutes.Notifications.Base, cancellationToken).ConfigureAwait(false);
        await CliHttpErrors.EnsureSuccessAsync(response, "Reading notifications", cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<List<NotificationEntry>>(CliJson.Options, cancellationToken).ConfigureAwait(false) ?? [];
    }
}
