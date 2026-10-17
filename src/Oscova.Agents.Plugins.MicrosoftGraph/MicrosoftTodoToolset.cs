using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Graph.Models;
using Oscova.Agents.Harness;
using Oscova.Agents.Policies;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Plugins.MicrosoftGraph;

/// <summary>
/// Exposes Microsoft To Do tools backed by Microsoft Graph and a scoped account token.
/// </summary>
public sealed partial class MicrosoftTodoToolset : IAgentToolset
{
    private const string NotConnectedMessage = "Microsoft To Do is not connected. Ask the user to connect their Microsoft account through the host connected accounts setup.";

    private readonly AgentPluginContext _context;
    private readonly ILogger<MicrosoftTodoToolset> _logger;

    /// <summary>Creates the per-user tools with host-provided account and time-zone services.</summary>
    public MicrosoftTodoToolset(AgentPluginContext context)
    {
        _context = context;
        _logger = context.LoggerFactory.CreateLogger<MicrosoftTodoToolset>();
    }

    /// <summary>
    /// Lists the user's Microsoft To Do task lists and returns the list IDs that can be reused in later calls.
    /// </summary>
    [DisplayName("list_task_lists")]
    [ToolNetworkScope(NetworkScope.Egress)]
    [AgentTool]
    [Description("""
        Lists the user's Microsoft To Do task lists.
        Returns task list IDs, display names, sharing details, and any well-known list name when available.
        Use the returned list IDs with list_tasks, create, update, and delete.
        """)]
    public async Task<string> ListTaskListsAsync(
        [Description("Maximum number of task lists to return (default 20, max 50).")] int maxResults = 20,
        CancellationToken ct = default)
    {
        return await MicrosoftGraphExecution.ExecuteAsync(async () =>
        {
            maxResults = Math.Clamp(maxResults, 1, 50);

            using var client = await MicrosoftGraphClientFactory.CreateAsync(_context, ct).ConfigureAwait(false);
            if (client is null)
            {
                throw new ToolFailureException(NotConnectedMessage);
            }

            var response = await client.Me.Todo.Lists.GetAsync(request => request.QueryParameters.Top = maxResults, cancellationToken: ct).ConfigureAwait(false);

            var lists = response?.Value;
            if (lists is null || lists.Count == 0)
            {
                return "No Microsoft To Do task lists were found.";
            }

            var builder = new StringBuilder();
            builder.AppendLine($"Showing {lists.Count} task list(s):")
                .AppendLine();

            for (var index = 0; index < lists.Count; index++)
            {
                var list = lists[index];
                builder.AppendLine($"[{index + 1}] ID: {list.Id ?? "(unknown)"}")
                    .AppendLine($"    Name: {list.DisplayName ?? "(no name)"}");

                if (list.WellknownListName is not null)
                {
                    builder.AppendLine($"    Well-known: {list.WellknownListName}");
                }

                if (list.IsOwner is not null)
                {
                    builder.AppendLine($"    Owner: {(list.IsOwner.Value ? "yes" : "no")}");
                }

                if (list.IsShared is not null)
                {
                    builder.AppendLine($"    Shared: {(list.IsShared.Value ? "yes" : "no")}");
                }

                builder.AppendLine();
            }

            return builder.ToString().TrimEnd();
        }, _logger, "Microsoft To Do", NotConnectedMessage).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists tasks from a specific Microsoft To Do task list so the agent can inspect IDs and current status.
    /// </summary>
    [DisplayName("list_tasks")]
    [ToolNetworkScope(NetworkScope.Egress)]
    [AgentTool]
    [Description("""
        Lists tasks in a specific Microsoft To Do task list.
        Returns each task ID, title, status, importance, due date, and a short note preview when available.
        Use list_task_lists first if you need a task list ID.
        """)]
    public async Task<string> ListTasksAsync(
        [Description("The Microsoft To Do task list ID obtained from list_task_lists.")] string taskListId,
        [Description("Maximum number of tasks to return (default 25, max 50).")] int maxResults = 25,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskListId))
        {
            throw new ToolFailureException("A Microsoft To Do task list ID is required.");
        }

        return await MicrosoftGraphExecution.ExecuteAsync(async () =>
        {
            maxResults = Math.Clamp(maxResults, 1, 50);

            using var client = await MicrosoftGraphClientFactory.CreateAsync(_context, ct).ConfigureAwait(false);
            if (client is null)
            {
                throw new ToolFailureException(NotConnectedMessage);
            }

            var response = await client.Me.Todo.Lists[taskListId.Trim()].Tasks.GetAsync(request => request.QueryParameters.Top = maxResults, cancellationToken: ct).ConfigureAwait(false);

            var tasks = response?.Value;
            if (tasks is null || tasks.Count == 0)
            {
                return "No tasks were found in that Microsoft To Do list.";
            }

            var builder = new StringBuilder();
            builder.AppendLine($"Showing {tasks.Count} task(s):")
                .AppendLine($"Task list ID: {taskListId.Trim()}")
                .AppendLine();

            for (var index = 0; index < tasks.Count; index++)
            {
                var task = tasks[index];
                builder.AppendLine($"[{index + 1}] ID: {task.Id ?? "(unknown)"}")
                    .AppendLine($"    Title: {task.Title ?? "(no title)"}")
                    .AppendLine($"    Status: {FormatStatusToken(task.Status) ?? "(unknown)"}");

                if (task.Importance is not null)
                {
                    builder.AppendLine($"    Importance: {FormatImportanceToken(task.Importance)}");
                }

                var dueDate = FormatGraphDate(task.DueDateTime);
                if (!string.IsNullOrWhiteSpace(dueDate))
                {
                    builder.AppendLine($"    Due: {dueDate}");
                }

                var preview = ToSingleLine(StripHtml(task.Body?.Content), 220);
                if (!string.IsNullOrWhiteSpace(preview))
                {
                    builder.AppendLine($"    Notes: {preview}");
                }

                builder.AppendLine();
            }

            return builder.ToString().TrimEnd();
        }, _logger, "Microsoft To Do", NotConnectedMessage).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a new Microsoft To Do task in the specified list.
    /// </summary>
    [DisplayName("create")]
    [ToolNetworkScope(NetworkScope.Egress)]
    [AgentTool]
    [Description("""
        Creates a new task in a Microsoft To Do task list.
        You must provide a task list ID and title.
        Optional note content is stored as HTML, and dueDateTime should be in ISO 8601 format when provided.
        Example dueDateTime: "2026-04-04T18:00:00Z"
        """)]
    public async Task<string> CreateTaskAsync(
        [Description("The Microsoft To Do task list ID obtained from list_task_lists.")] string taskListId,
        [Description("The title of the new task.")] string title,
        [Description("Optional task note content.")] string? body = null,
        [Description("Optional due date/time in ISO 8601 format, for example '2026-04-04T18:00:00Z'.")] string? dueDateTime = null,
        [Description("Optional importance value: low, normal, or high. Defaults to normal.")] string importance = "normal",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskListId))
        {
            throw new ToolFailureException("A Microsoft To Do task list ID is required.");
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ToolFailureException("A Microsoft To Do task title is required.");
        }

        if (!TryParseImportance(importance, out var parsedImportance))
        {
            throw new ToolFailureException("The task importance is invalid.", "Use low, normal, or high.");
        }

        var dueDate = TryCreateGraphDateTime(dueDateTime, _context.UserTimeZone);
        if (dueDateTime is not null && dueDate is null)
        {
            throw new ToolFailureException("The task due date is invalid.", "Use an ISO 8601 date and time.");
        }

        return await MicrosoftGraphExecution.ExecuteAsync(async () =>
        {
            using var client = await MicrosoftGraphClientFactory.CreateAsync(_context, ct).ConfigureAwait(false);
            if (client is null)
            {
                throw new ToolFailureException(NotConnectedMessage);
            }

            var todoTask = new TodoTask
            {
                Title = title.Trim(),
                Importance = parsedImportance,
                Body = string.IsNullOrWhiteSpace(body) ? null : CreateHtmlBody(body),
                DueDateTime = dueDate
            };

            var task = await client.Me.Todo.Lists[taskListId.Trim()].Tasks.PostAsync(todoTask, cancellationToken: ct).ConfigureAwait(false);
            if (task is null)
            {
                throw new ToolFailureException(
                    "The task may have been created, but Microsoft To Do did not confirm it.",
                    "Refresh the task list before trying again.");
            }

            var builder = new StringBuilder();
            builder.AppendLine("Task created.")
                .AppendLine($"ID: {task.Id ?? "(unknown)"}")
                .AppendLine($"Title: {task.Title ?? title.Trim()}")
                .AppendLine($"Status: {FormatStatusToken(task.Status) ?? "notStarted"}");

            var createdDueDate = FormatGraphDate(task.DueDateTime);
            if (!string.IsNullOrWhiteSpace(createdDueDate))
            {
                builder.AppendLine($"Due: {createdDueDate}");
            }

            return builder.ToString().TrimEnd();
        }, _logger, "Microsoft To Do", NotConnectedMessage).ConfigureAwait(false);
    }

    /// <summary>
    /// Updates an existing Microsoft To Do task using only the fields the caller supplies.
    /// </summary>
    [DisplayName("update")]
    [ToolNetworkScope(NetworkScope.Egress)]
    [AgentTool]
    [Description("""
        Updates an existing Microsoft To Do task.
        Supply only the fields you want to change.
        Status must be one of: notStarted, inProgress, completed, waitingOnOthers, deferred.
        Importance must be one of: low, normal, high.
        dueDateTime should be ISO 8601 when provided.
        """)]
    public async Task<string> UpdateTaskAsync(
        [Description("The Microsoft To Do task list ID.")] string taskListId,
        [Description("The Microsoft To Do task ID.")] string taskId,
        [Description("Optional updated title.")] string? title = null,
        [Description("Optional updated task note content.")] string? body = null,
        [Description("Optional updated status: notStarted, inProgress, completed, waitingOnOthers, deferred.")] string? status = null,
        [Description("Optional updated due date/time in ISO 8601 format.")] string? dueDateTime = null,
        [Description("Optional updated importance: low, normal, or high.")] string? importance = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskListId))
        {
            throw new ToolFailureException("A Microsoft To Do task list ID is required.");
        }

        if (string.IsNullOrWhiteSpace(taskId))
        {
            throw new ToolFailureException("A Microsoft To Do task ID is required.");
        }

        if (string.IsNullOrWhiteSpace(title) &&
            string.IsNullOrWhiteSpace(body) &&
            string.IsNullOrWhiteSpace(status) &&
            string.IsNullOrWhiteSpace(dueDateTime) &&
            string.IsNullOrWhiteSpace(importance))
        {
            throw new ToolFailureException("At least one task field must be provided.");
        }

        Microsoft.Graph.Models.TaskStatus parsedStatus = default;
        if (!string.IsNullOrWhiteSpace(status) && !TryParseStatus(status, out parsedStatus))
        {
            throw new ToolFailureException(
                "The task status is invalid.",
                "Use notStarted, inProgress, completed, waitingOnOthers, or deferred.");
        }

        Importance parsedImportance = default;
        if (!string.IsNullOrWhiteSpace(importance) && !TryParseImportance(importance, out parsedImportance))
        {
            throw new ToolFailureException("The task importance is invalid.", "Use low, normal, or high.");
        }

        var dueDate = string.IsNullOrWhiteSpace(dueDateTime) ? null : TryCreateGraphDateTime(dueDateTime, _context.UserTimeZone);
        if (!string.IsNullOrWhiteSpace(dueDateTime) && dueDate is null)
        {
            throw new ToolFailureException("The task due date is invalid.", "Use an ISO 8601 date and time.");
        }

        return await MicrosoftGraphExecution.ExecuteAsync(async () =>
        {
            using var client = await MicrosoftGraphClientFactory.CreateAsync(_context, ct).ConfigureAwait(false);
            if (client is null)
            {
                throw new ToolFailureException(NotConnectedMessage);
            }

            var todoTask = new TodoTask
            {
                Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
                Body = string.IsNullOrWhiteSpace(body) ? null : CreateHtmlBody(body),
                Status = string.IsNullOrWhiteSpace(status) ? null : parsedStatus,
                Importance = string.IsNullOrWhiteSpace(importance) ? null : parsedImportance,
                DueDateTime = dueDate
            };

            var task = await client.Me.Todo.Lists[taskListId.Trim()].Tasks[taskId.Trim()].PatchAsync(todoTask, cancellationToken: ct).ConfigureAwait(false);
            if (task is null)
            {
                throw new ToolFailureException(
                    "The task may have been updated, but Microsoft To Do did not confirm it.",
                    "Refresh the task list before trying again.");
            }

            var builder = new StringBuilder();
            builder.AppendLine("Task updated.")
                .AppendLine($"ID: {task.Id ?? taskId.Trim()}")
                .AppendLine($"Title: {task.Title ?? "(no title)"}")
                .AppendLine($"Status: {FormatStatusToken(task.Status) ?? "(unknown)"}");

            if (task.Importance is not null)
            {
                builder.AppendLine($"Importance: {FormatImportanceToken(task.Importance)}");
            }

            var updatedDueDate = FormatGraphDate(task.DueDateTime);
            if (!string.IsNullOrWhiteSpace(updatedDueDate))
            {
                builder.AppendLine($"Due: {updatedDueDate}");
            }

            return builder.ToString().TrimEnd();
        }, _logger, "Microsoft To Do", NotConnectedMessage).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a Microsoft To Do task after the caller explicitly confirms the deletion.
    /// </summary>
    [DisplayName("delete")]
    [ToolNetworkScope(NetworkScope.Egress)]
    [ToolPolicy(ApprovalMode = ToolApprovalMode.RequireApproval)]
    [AgentTool]
    [Description("""
        Deletes a Microsoft To Do task from the specified list.
        Set confirmDelete=true only when you are sure the task should be permanently removed.
        """)]
    public async Task<string> DeleteTaskAsync(
        [Description("The Microsoft To Do task list ID.")] string taskListId,
        [Description("The Microsoft To Do task ID.")] string taskId,
        [Description("Must be true to confirm permanent deletion.")] bool confirmDelete = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskListId))
        {
            throw new ToolFailureException("A Microsoft To Do task list ID is required.");
        }

        if (string.IsNullOrWhiteSpace(taskId))
        {
            throw new ToolFailureException("A Microsoft To Do task ID is required.");
        }

        if (!confirmDelete)
        {
            throw new ToolFailureException(
                "Task deletion was not confirmed.",
                "Set confirmDelete to true if the task should be removed.");
        }

        return await MicrosoftGraphExecution.ExecuteAsync(async () =>
        {
            using var client = await MicrosoftGraphClientFactory.CreateAsync(_context, ct).ConfigureAwait(false);
            if (client is null)
            {
                throw new ToolFailureException(NotConnectedMessage);
            }

            await client.Me.Todo.Lists[taskListId.Trim()].Tasks[taskId.Trim()].DeleteAsync(cancellationToken: ct).ConfigureAwait(false);
            return "Task deleted.";
        }, _logger, "Microsoft To Do", NotConnectedMessage).ConfigureAwait(false);
    }

    /// <summary>
    /// Formats a Graph dateTimeTimeZone value into readable user-local output when possible.
    /// </summary>
    private string? FormatGraphDate(DateTimeTimeZone? value)
    {
        if (value is null || string.IsNullOrWhiteSpace(value.DateTime))
        {
            return null;
        }

        var dateTime = value.DateTime;
        var timeZone = value.TimeZone;
        var isUtcTimeZone = string.IsNullOrWhiteSpace(timeZone) ||
            string.Equals(timeZone, "UTC", StringComparison.OrdinalIgnoreCase);
        var userTimeZone = _context.UserTimeZone;

        // Graph dateTimeTimeZone values can represent a local wall-clock time plus a separate
        // time zone, so only force UTC conversion when the payload already carries UTC/offset
        // information or explicitly says the time zone is UTC.
        if (HasExplicitUtcOrOffset(dateTime))
        {
            if (DateTimeOffset.TryParse(dateTime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var explicitOffsetValue))
            {
                return TimeZoneInfo.ConvertTime(explicitOffsetValue, userTimeZone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            }
        }
        else if (isUtcTimeZone)
        {
            if (DateTimeOffset.TryParse(dateTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var utcValue))
            {
                return TimeZoneInfo.ConvertTime(utcValue, userTimeZone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            }
        }
        else
        {
            var normalizedTimeZoneId = TimeZoneResolver.NormalizeOrEmpty(timeZone);
            if (!string.IsNullOrWhiteSpace(normalizedTimeZoneId) &&
                DateTime.TryParse(dateTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var localWallClockTime))
            {
                try
                {
                    var providerTimeZone = TimeZoneInfo.FindSystemTimeZoneById(normalizedTimeZoneId);
                    var unspecifiedTime = DateTime.SpecifyKind(localWallClockTime, DateTimeKind.Unspecified);
                    var providerDateTime = new DateTimeOffset(unspecifiedTime, providerTimeZone.GetUtcOffset(unspecifiedTime));
                    return TimeZoneInfo.ConvertTime(providerDateTime, userTimeZone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                }
                catch (TimeZoneNotFoundException)
                {
                    // Fall back to the original date/time string below when the provider time
                    // zone is not available on the current platform.
                }
                catch (InvalidTimeZoneException)
                {
                    // Fall back to the original date/time string below when the provider time
                    // zone data is invalid.
                }
            }
        }

        return string.IsNullOrWhiteSpace(timeZone)
            ? dateTime
            : $"{dateTime} ({timeZone})";
    }

    /// <summary>
    /// Detects whether a Graph date/time string already carries an explicit UTC or offset marker.
    /// </summary>
    private static bool HasExplicitUtcOrOffset(string value)
    {
        return value.EndsWith("Z", StringComparison.OrdinalIgnoreCase) ||
            UtcOffsetRegex().IsMatch(value);
    }

    /// <summary>
    /// Creates a Graph dateTimeTimeZone payload as wall-clock time in the user's time zone.
    /// </summary>
    private static DateTimeTimeZone? TryCreateGraphDateTime(string? value, TimeZoneInfo userTimeZone)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return null;
        }

        // Offset-less input is already the user's wall clock. To Do keeps the due day of that wall clock.
        var wallClock = parsed.Kind == DateTimeKind.Unspecified ? parsed : TimeZoneInfo.ConvertTime(parsed, userTimeZone);
        var timeZoneId = userTimeZone.HasIanaId && TimeZoneInfo.TryConvertIanaIdToWindowsId(userTimeZone.Id, out var windowsId)
            ? windowsId
            : userTimeZone.Id;
        return new DateTimeTimeZone
        {
            DateTime = wallClock.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            TimeZone = timeZoneId
        };
    }

    /// <summary>
    /// Wraps task note text into an HTML body for Microsoft Graph.
    /// </summary>
    private static ItemBody CreateHtmlBody(string body)
    {
        var encoded = WebUtility.HtmlEncode(body.Trim())
            .Replace("\r\n", "<br/>", StringComparison.Ordinal)
            .Replace("\n", "<br/>", StringComparison.Ordinal);

        return new ItemBody
        {
            ContentType = BodyType.Html,
            Content = $"<div>{encoded}</div>"
        };
    }

    /// <summary>
    /// Converts multi-line text into a single concise line for compact tool output.
    /// </summary>
    private static string? ToSingleLine(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var collapsed = value
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

        if (collapsed.Length <= maxLength)
        {
            return collapsed;
        }

        return $"{collapsed[..maxLength].TrimEnd()}...";
    }

    /// <summary>
    /// Removes the most common HTML tags from Graph task note content before listing it back to the agent.
    /// </summary>
    private static string? StripHtml(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var withoutTags = HtmlTagRegex().Replace(value, " ");
        return WebUtility.HtmlDecode(withoutTags);
    }

    private static bool TryParseImportance(string value, out Importance parsed)
    {
        parsed = value.Trim().ToLowerInvariant() switch
        {
            "low" => Importance.Low,
            "normal" => Importance.Normal,
            "high" => Importance.High,
            _ => default
        };

        return value.Trim().ToLowerInvariant() is "low" or "normal" or "high";
    }

    private static bool TryParseStatus(string value, out Microsoft.Graph.Models.TaskStatus parsed)
    {
        var normalizedValue = value.Trim().ToLowerInvariant();

        parsed = normalizedValue switch
        {
            "notstarted" => Microsoft.Graph.Models.TaskStatus.NotStarted,
            "inprogress" => Microsoft.Graph.Models.TaskStatus.InProgress,
            "completed" => Microsoft.Graph.Models.TaskStatus.Completed,
            "waitingonothers" => Microsoft.Graph.Models.TaskStatus.WaitingOnOthers,
            "deferred" => Microsoft.Graph.Models.TaskStatus.Deferred,
            _ => default
        };

        return normalizedValue is "notstarted" or "inprogress" or "completed" or "waitingonothers" or "deferred";
    }

    /// <summary>
    /// Formats Graph task status values using the same tokens the tool accepts as input.
    /// </summary>
    private static string? FormatStatusToken(Microsoft.Graph.Models.TaskStatus? value)
    {
        return value switch
        {
            Microsoft.Graph.Models.TaskStatus.NotStarted => "notStarted",
            Microsoft.Graph.Models.TaskStatus.InProgress => "inProgress",
            Microsoft.Graph.Models.TaskStatus.Completed => "completed",
            Microsoft.Graph.Models.TaskStatus.WaitingOnOthers => "waitingOnOthers",
            Microsoft.Graph.Models.TaskStatus.Deferred => "deferred",
            _ => value?.ToString()
        };
    }

    /// <summary>
    /// Formats Graph importance values using the same lowercase tokens the tool accepts as input.
    /// </summary>
    private static string? FormatImportanceToken(Importance? value)
    {
        return value?.ToString()?.ToLowerInvariant();
    }

    [GeneratedRegex("[+-]\\d{2}:\\d{2}$", RegexOptions.None, 100)]
    private static partial Regex UtcOffsetRegex();

    [GeneratedRegex("<[^>]+>", RegexOptions.Compiled, 100)]
    private static partial Regex HtmlTagRegex();

}
