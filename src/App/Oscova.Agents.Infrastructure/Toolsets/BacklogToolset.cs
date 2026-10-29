using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.FileBackups;
using Oscova.Agents.Harness;
using Oscova.Agents.Memory;
using Oscova.Agents.Secrets;

namespace Oscova.Agents.Infrastructure.Toolsets;

/// <summary>Keeps the user's to-do backlog that any agent can add to and the Night Shift task works.</summary>
public sealed class BacklogToolset(string userId, IContentProtector protector, IUserDirectoryService userDirectories,
    RunContextAccessor runContextAccessor, IUserFileBackupService fileBackups, IElicitationRouter elicitationRouter,
    ILogger<BacklogToolset> logger) : IAgentToolset
{
    private const int PageSize = 20;
    private const string Cancelled = "Deletion cancelled by the user. Every item was retained. Do not retry unless the user asks again.";
    private static readonly string[] ListStates = [BacklogFile.OpenState, BacklogFile.DoneState, BacklogFile.WithdrawnState, "all"];

    /// <summary>Lists one page of items in a state, newest first.</summary>
    [DisplayName("list")]
    [AgentTool]
    [Description("List items on the user's to-do list.\nstate: open (default), done, withdrawn or all.\nReturns at most 20 items, newest first. Use offset for the next page.")]
    public Task<string> ListAsync(
        [Description("open, done, withdrawn or all. Default: open.")] string state = BacklogFile.OpenState,
        [Description("Number of items to skip. Default 0.")] int offset = 0,
        CancellationToken ct = default) => ExecuteAsync("list", async () =>
    {
        state = state.Trim().ToLowerInvariant();
        if (!ListStates.Contains(state, StringComparer.Ordinal))
        {
            throw new ToolFailureException($"state must be one of: {string.Join(", ", ListStates)}.");
        }

        offset = Math.Max(offset, 0);
        var items = (await BacklogFile.ReadItemsAsync(GetStore(), ct).ConfigureAwait(false))
            .Where(item => state == "all" || item.State == state)
            .OrderByDescending(item => item.Added).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
        if (items.Length == 0) { return $"No {state} items."; }

        var page = items.Skip(offset).Take(PageSize).ToArray();
        var lines = new List<string> { "conceptId | title | assignee | added" + (state == "all" ? " | state" : string.Empty) };
        lines.AddRange(page.Select(item => $"- {item.Id} | {item.Title} | {item.Assignee} | {BacklogItem.Format(item.Added)}" +
            (state == "all" ? $" | {item.State}" : string.Empty)));
        var remaining = items.Length - offset - page.Length;
        if (remaining > 0) { lines.Add($"{remaining} more, call list with offset {offset + page.Length}"); }

        return string.Join('\n', lines);
    }, ct);

    /// <summary>Creates an item or updates the supplied fields of an existing one.</summary>
    [DisplayName("save")]
    [AgentTool]
    [Description("Add or update one item on the user's to-do list.\nUser asks you to do something tonight or later with no clock time: save it with assignee agent. Night Shift works it overnight, so do not do or schedule it now.\nA request with a clock time, such as tomorrow at 15:00, is a scheduled task, not a to-do.\nassignee user (default): the user will do it.\nReuse a discovered conceptId to update. Choose a new short ID for a new item, which requires title.\nSet state withdrawn when the user drops an item.")]
    public Task<string> SaveAsync(
        [Description("Item ID without .md. Reuse a discovered ID to update. Choose a new ID to create.")] string conceptId,
        [Description("Short item title. Required for a new item.")] string? title = null,
        [Description("user or agent. Default for a new item: user.")] string? assignee = null,
        [Description("Optional detail the item needs.")] string? description = null,
        [Description("open or withdrawn. Omit to keep the current state. New items start open.")] string? state = null,
        CancellationToken ct = default) => ExecuteAsync("save", async () =>
    {
        assignee = Choice(assignee, nameof(assignee), BacklogFile.UserAssignee, BacklogFile.AgentAssignee);
        state = Choice(state, nameof(state), BacklogFile.OpenState, BacklogFile.WithdrawnState);
        var store = GetStore();
        var id = store.ResolveConceptId(conceptId);
        if (await store.ReadConceptAsync(id, ct).ConfigureAwait(false) is null)
        {
            if (string.IsNullOrWhiteSpace(title)) { throw new ToolFailureException("title is required for a new item."); }

            var fields = new Dictionary<string, object?>
            {
                ["type"] = BacklogFile.ItemType,
                ["title"] = title.Trim(),
                ["assignee"] = assignee ?? BacklogFile.UserAssignee,
                ["state"] = state ?? BacklogFile.OpenState,
                ["added"] = Now(),
                ["agent"] = runContextAccessor.Current?.CurrentAgentId ?? throw new ToolFailureException("An active agent run is required.")
            };
            if (!string.IsNullOrWhiteSpace(description)) { fields["description"] = description.Trim(); }

            await store.SaveAsync(id, fields, string.Empty, ct).ConfigureAwait(false);
            return $"Saved to-do '{title.Trim()}' (conceptId: {id}, assignee: {fields["assignee"]}, state: {fields["state"]}).";
        }

        var result = await store.UpdateAsync(id, fields =>
        {
            // A closed item never absorbs a new request that reuses its ID.
            var current = Convert.ToString(fields.TryGetValue("state", out var value) ? value : null, CultureInfo.InvariantCulture);
            if (state is null && current != BacklogFile.OpenState)
            {
                throw new ToolFailureException($"Item {id} is {current}, so nothing was saved.", "Choose a new conceptId for a new item, or pass state open to reopen it.");
            }

            if (!string.IsNullOrWhiteSpace(title)) { fields["title"] = title.Trim(); }

            if (assignee is not null) { fields["assignee"] = assignee; }

            if (state is not null) { fields["state"] = state; }

            if (!string.IsNullOrWhiteSpace(description)) { fields["description"] = description.Trim(); }

            return null;
        }, ct).ConfigureAwait(false);
        return result.StartsWith("Updated", StringComparison.Ordinal) ? $"Updated to-do (conceptId: {id})." : result;
    }, ct);

    /// <summary>Marks one open item done with a line of evidence, never recreating a removed item.</summary>
    [DisplayName("complete")]
    [AgentTool]
    [Description("Mark one open item on the user's to-do list done.\nUse a conceptId returned by list or save.\nevidence: one line saying what was done.\nDone or withdrawn items stay unchanged.")]
    public Task<string> CompleteAsync(
        [Description("Existing item ID without .md.")] string conceptId,
        [Description("One line saying what was done.")] string evidence,
        CancellationToken ct = default) => ExecuteAsync("complete", async () =>
    {
        if (string.IsNullOrWhiteSpace(evidence)) { throw new ToolFailureException("evidence is required.", "Say in one line what was done."); }

        var line = string.Join(' ', evidence.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var store = GetStore();
        var id = store.ResolveConceptId(conceptId);
        var result = await store.UpdateAsync(id, fields =>
        {
            var current = Convert.ToString(fields.TryGetValue("state", out var value) ? value : null, CultureInfo.InvariantCulture);
            if (current != BacklogFile.OpenState) { return $"Item {id} is already {current}. Nothing changed."; }

            fields["state"] = BacklogFile.DoneState;
            fields["completed"] = Now();
            fields["evidence"] = line;
            return null;
        }, ct).ConfigureAwait(false);
        return result.StartsWith("Updated", StringComparison.Ordinal) ? $"Completed to-do (conceptId: {id})." : result;
    }, ct);

    /// <summary>Deletes one item, the done items or every item after the user confirms.</summary>
    [DisplayName("remove")]
    [AgentTool]
    [Description("Delete items from the user's to-do list after the user confirms.\nSupply exactly one: conceptId for one item, or scope done or all.\nscope done deletes finished items. scope all deletes every item.\nIf the user declines or cancels, stop. Do not retry.")]
    public Task<string> RemoveAsync(
        [Description("Existing item ID without .md. Omit when scope is supplied.")] string? conceptId = null,
        [Description("done or all. Omit when conceptId is supplied.")] string? scope = null,
        CancellationToken ct = default) => ExecuteAsync("remove", async () =>
    {
        var store = GetStore();
        var selected = DeletionConfirmation.RequireOneTarget(conceptId, scope, BacklogFile.DoneState, "all");
        if (selected is null)
        {
            var id = store.ResolveConceptId(conceptId!);
            if (await store.ReadConceptAsync(id, ct).ConfigureAwait(false) is not { } concept) { return $"Concept not found: {id}."; }

            var title = new BacklogItem(id, concept.Metadata, concept.Body).Title;
            return await DeletionConfirmation.ConfirmAsync(runContextAccessor, elicitationRouter, $"Delete to-do '{title}'?", ct).ConfigureAwait(false)
                ? await store.DeleteAsync(id, ct).ConfigureAwait(false)
                : Cancelled;
        }

        var items = (await BacklogFile.ReadItemsAsync(store, ct).ConfigureAwait(false))
            .Where(item => selected == "all" || item.State == BacklogFile.DoneState).ToArray();
        if (items.Length == 0) { return selected == "all" ? "No to-do items to delete." : "No done to-do items to delete."; }

        var message = selected == "all"
            ? $"Delete {DeletionConfirmation.Count(items.Length, "to-do item", "to-do items")}?"
            : $"Delete {DeletionConfirmation.Count(items.Length, "done to-do item", "done to-do items", all: false)}?";
        if (!await DeletionConfirmation.ConfirmAsync(runContextAccessor, elicitationRouter, message, ct).ConfigureAwait(false)) { return Cancelled; }

        if (selected == "all") { return await store.DeleteAllAsync(null, snapshot: true, ct).ConfigureAwait(false); }

        foreach (var item in items) { await store.DeleteAsync(item.Id, ct).ConfigureAwait(false); }

        return $"Deleted {items.Length} done to-do items.";
    }, ct);

    private EncryptedKnowledgeStore GetStore() => BacklogFile.CreateStore(userId, userDirectories, protector, fileBackups);

    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static string? Choice(string? value, string name, params string[] allowed)
    {
        if (string.IsNullOrWhiteSpace(value)) { return null; }

        var normalized = value.Trim().ToLowerInvariant();
        return allowed.Contains(normalized, StringComparer.Ordinal)
            ? normalized
            : throw new ToolFailureException($"{name} must be one of: {string.Join(", ", allowed)}.");
    }

    private async Task<string> ExecuteAsync(string operation, Func<Task<string>> action, CancellationToken ct)
    {
        try { return await action().ConfigureAwait(false); }
        catch (ToolFailureException) { throw; }
        catch (AgentMemoryValidationException ex) { throw new ToolFailureException(ex.Message); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (UnauthorizedAccessException)
        {
            throw new ToolFailureException("The requested to-do path is not authorized.", "Use a conceptId returned by list or save.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Backlog operation {Operation} failed for user {UserId}", operation, userId);
            throw new ToolFailureException($"To-do {operation} is temporarily unavailable. Check the Assistant log.");
        }
    }
}
