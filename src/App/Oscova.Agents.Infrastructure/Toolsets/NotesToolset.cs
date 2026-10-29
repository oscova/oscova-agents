using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.FileBackups;
using Oscova.Agents.Harness;
using Oscova.Agents.Memory;
using Oscova.Agents.Secrets;

namespace Oscova.Agents.Infrastructure.Toolsets;

/// <summary>Discovers and persists evidenced behavioral lessons through the shared encrypted knowledge store.</summary>
public sealed class NotesToolset(string userId, IContentProtector protector, IUserDirectoryService userDirectories,
    RunContextAccessor runContextAccessor, IUserFileBackupService fileBackups, IElicitationRouter elicitationRouter,
    ILogger<NotesToolset> logger) : IAgentToolset
{
    /// <summary>Finds existing lessons before creating or updating a private note.</summary>
    [DisplayName("find")]
    [AgentTool]
    [Description("Find your saved behavioral lessons, not user facts or preferences.\nSearches titles, descriptions, tags and bodies.\nquery must contain a word with at least 2 letters or digits.")]
    public Task<string> FindNotesAsync(
        [Description("Words identifying the lesson. Include a word with at least 2 letters or digits.")] string query,
        CancellationToken ct = default) => ExecuteAsync("find", () => GetStore().FindAsync(query, ct: ct), ct);

    /// <summary>Reads the private entry index or one discovered lesson/router.</summary>
    [DisplayName("read")]
    [AgentTool]
    [Description("Read your private lesson index or one saved lesson/router.\nOmit document for the entry index.\nUse a reference returned by find, read or save.")]
    public Task<string> ReadNotesAsync(
        [Description("Existing concept ID or logical .md path. Omit for the entry index.")] string? document = null,
        CancellationToken ct = default) => ExecuteAsync("read", async () =>
            await GetStore().ReadAsync(document, ct).ConfigureAwait(false)
            ?? throw new ToolFailureException("document was not found.", "Read the index or find the current reference."), ct);

    /// <summary>Saves an evidenced behavioral lesson without replacing other notes.</summary>
    [DisplayName("save")]
    [AgentTool]
    [Description("Save a behavioral lesson from a demonstrated successful method.\nSave user facts and preferences with memory tools.\nFind/read existing lessons first. Skip unchanged duplicates.\nMaximum complete document: 524288 UTF-8 bytes.")]
    public Task<string> SaveNotesAsync(
        [Description("Bundle-relative ID without .md. Reuse a discovered ID to update. Choose a new ID to create.\nindex and log are reserved. Host-absolute paths and escaping paths are forbidden.")] string conceptId,
        [Description("Metadata object.\nNew concepts require frontmatter.type: a nonempty string.\nInclude title and description for discovery.\nDo not supply generated or verified.")] IReadOnlyDictionary<string, object?> frontmatter,
        [Description("Complete lesson Markdown body. The body and metadata together must fit 524288 UTF-8 bytes.")] string content,
        CancellationToken ct = default) => ExecuteAsync("save", () => GetStore().SaveAsync(conceptId, frontmatter, content, ct), ct);

    /// <summary>Deletes one lesson, or every lesson, after the user confirms.</summary>
    [DisplayName("forget")]
    [AgentTool]
    [Description("Delete your saved lessons after the user confirms.\nSupply exactly one: conceptId for one lesson, or scope all for every lesson.\nUse a conceptId returned by find, read or save.\nIf the user declines or cancels, stop. Do not retry.")]
    public Task<string> ForgetNotesAsync(
        [Description("Existing lesson ID without .md. Omit when scope is all.")] string? conceptId = null,
        [Description("all deletes every lesson. Omit when conceptId is supplied.")] string? scope = null,
        CancellationToken ct = default) => ExecuteAsync("forget", async () =>
    {
        var store = GetStore();
        if (DeletionConfirmation.RequireOneTarget(conceptId, scope, "all") is not null)
        {
            var count = store.ListConceptIds(string.Empty).Count;
            if (count == 0) { return "No saved lessons to delete."; }

            // Notes keep per-file history only: a sibling snapshot would sit inside the agent's own private directory.
            return await DeletionConfirmation.ConfirmAsync(runContextAccessor, elicitationRouter,
                $"Delete {DeletionConfirmation.Count(count, "private lesson", "private lessons")} of this agent?", ct).ConfigureAwait(false)
                ? await store.DeleteAllAsync(null, snapshot: false, ct).ConfigureAwait(false)
                : "Deletion cancelled by the user. Every lesson was retained. Do not retry unless the user asks again.";
        }

        var id = store.ResolveConceptId(conceptId!);
        if (await store.ReadConceptAsync(id, ct).ConfigureAwait(false) is null) { return $"Concept not found: {id}."; }

        return await DeletionConfirmation.ConfirmAsync(runContextAccessor, elicitationRouter,
            $"Delete private lesson '{id}'?", ct).ConfigureAwait(false)
            ? await store.DeleteAsync(id, ct).ConfigureAwait(false)
            : "Deletion cancelled by the user. The selected lesson was retained. Do not retry unless the user asks again.";
    }, ct);

    private EncryptedKnowledgeStore GetStore() => AgentNotesFile.CreateStore(userId,
        runContextAccessor.Current?.CurrentAgentId ?? throw new ToolFailureException("An active agent run is required."),
        userDirectories, protector, fileBackups);

    private async Task<string> ExecuteAsync(string operation, Func<Task<string>> action, CancellationToken ct)
    {
        try { return await action().ConfigureAwait(false); }
        catch (ToolFailureException) { throw; }
        catch (AgentMemoryValidationException ex) { throw new ToolFailureException(ex.Message); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (UnauthorizedAccessException)
        {
            throw new ToolFailureException("Private notes are unavailable for this agent or path.", "Use references from your own notes index.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Notes operation {Operation} failed for user {UserId}", operation, userId);
            throw new ToolFailureException($"Notes {operation} is temporarily unavailable. Check the Assistant log.");
        }
    }
}