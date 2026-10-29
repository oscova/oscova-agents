using System.ComponentModel;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Memory;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Core.Utils;
using Oscova.Agents.Definitions;
using Oscova.Agents.Harness;
using Oscova.Agents.Infrastructure.Memory;
using Oscova.Agents.Memory;

namespace Oscova.Agents.Infrastructure.Toolsets;

/// <summary>Separates long-term concepts, short-term daily notes and indexed conversation recall.</summary>
public sealed class MemoryToolset(
    string userId,
    IChatMemoryService chatMemoryService,
    IAgentMemoryService agentMemoryService,
    AssistantUiSettings uiSettings,
    PrivacySettings privacySettings,
    IAgentDefinitionRepository agentDefinitions,
    RunContextAccessor runContextAccessor,
    IElicitationRouter elicitationRouter,
    ILogger<MemoryToolset> logger) : IAgentToolset
{
    /// <summary>Names the tools that change saved memory, which incognito chats never receive.</summary>
    public static IReadOnlySet<string> WriteToolNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ToolNaming.BuildToolsetQualifiedName<MemoryToolset>(nameof(SaveMemoryAsync)),
        ToolNaming.BuildToolsetQualifiedName<MemoryToolset>(nameof(SaveDailyMemoryAsync)),
        ToolNaming.BuildToolsetQualifiedName<MemoryToolset>(nameof(ForgetMemoryAsync))
    };

    /// <summary>Searches indexed historical conversation excerpts.</summary>
    [DisplayName("search")]
    [AgentTool]
    [Description("Search past conversations, not saved long-term concepts.\nFor saved facts and preferences, use find.\nmaxResults defaults to 5. Values below 1 use 5. Values above 50 use 50.")]
    public Task<string> SearchMemoryAsync(
        [Description("Words describing the past discussion.")] string query,
        [Description("Result count. Default 5. Values below 1 use 5. Values above 50 use 50.")] int maxResults = 5,
        CancellationToken ct = default) => ExecuteAsync("conversation search", async () =>
    {
        var agentId = CurrentAgentId();
        maxResults = maxResults <= 0 ? 5 : Math.Min(maxResults, 50);
        var agent = await agentDefinitions.GetAgentAsync(userId, agentId, ct).ConfigureAwait(false);
        var results = await chatMemoryService.SearchAsync(userId, agentId, query, maxResults,
            requireLocalEndpoint: privacySettings.IsLocalOnly || agent?.IsLocalOnly == true, ct: ct).ConfigureAwait(false);
        if (results.UnavailableReason is { } reason) { return reason; }

        if (results.Matches.Count == 0) { return "No relevant indexed conversation excerpts found."; }

        return FormatResults(results.Matches, includeScore: true);
    }, ct);

    /// <summary>Reads indexed excerpts from a previously discovered conversation.</summary>
    [DisplayName("get")]
    [AgentTool]
    [Description("Read indexed excerpts from a past conversation.\nUse a threadId returned by search.\nThis is not the full transcript.")]
    public Task<string> GetMemoryAsync(
        [Description("Thread ID returned by conversation search.")] string threadId,
        CancellationToken ct = default) => ExecuteAsync("conversation read", async () =>
    {
        var results = await chatMemoryService.GetByThreadIdAsync(userId, CurrentAgentId(), threadId, ct).ConfigureAwait(false);
        return results.Count == 0 ? "No indexed excerpts found for this thread." : FormatResults(results, includeScore: false);
    }, ct);

    /// <summary>Finds durable user knowledge in the encrypted long-term bundle.</summary>
    [DisplayName("find")]
    [AgentTool]
    [Description("Find saved long-term user facts, preferences and decisions.\nSearches titles, descriptions, tags and bodies.\nquery must contain a word with at least 2 letters or digits.")]
    public Task<string> FindMemoryAsync(
        [Description("Words identifying the saved knowledge. Include a word with at least 2 letters or digits.")] string query,
        CancellationToken ct = default) => ExecuteAsync("long-term find",
        () => agentMemoryService.GetStore(userId).FindAsync(query, AgentMemoryService.DailyDirectoryName, ct), ct);

    /// <summary>Reads one saved long-term concept or its directory router.</summary>
    [DisplayName("read")]
    [AgentTool]
    [Description("Read the long-term entry index or one saved concept/router.\nOmit document for the entry index.\nUse a reference returned by find, read or save.\nFor short-term daily logs, use read_daily.")]
    public Task<string> ReadMemoryAsync(
        [Description("Existing concept ID or logical .md path. Omit for the entry index.")] string? document = null,
        CancellationToken ct = default) => ExecuteAsync("long-term read", async () =>
    {
        var content = await agentMemoryService.GetStore(userId).ReadAsync(document, ct).ConfigureAwait(false);
        return content ?? throw new ToolFailureException("document was not found.", "Read the index or find the current reference.");
    }, ct);

    /// <summary>Saves one durable user concept while retaining its unmodified metadata.</summary>
    [DisplayName("save")]
    [AgentTool]
    [Description("Save only durable long-term user facts, preferences or decisions.\nTemporary or short-term information must use save_daily.\nRead an existing concept before updating it.\nMaximum complete document: 524288 UTF-8 bytes.")]
    public Task<string> SaveMemoryAsync(
        [Description("Bundle-relative ID without .md. Reuse a discovered ID to update. Choose a new ID to create.\nindex, log and daily/ are reserved. Host-absolute paths and escaping paths are forbidden.")] string conceptId,
        [Description("Metadata object.\nNew concepts require frontmatter.type: a nonempty string.\nInclude title and description for discovery.\nDo not supply generated or verified.")] IReadOnlyDictionary<string, object?> frontmatter,
        [Description("Complete Markdown body for this concept. The body and metadata together must fit 524288 UTF-8 bytes.")] string content,
        CancellationToken ct = default) => ExecuteAsync("long-term save", () =>
    {
        var store = agentMemoryService.GetStore(userId);
        var id = LongTermId(store, conceptId);
        return store.SaveAsync(id, frontmatter, content, ct);
    }, ct);

    /// <summary>Reads a short-term daily record using the user's local date.</summary>
    [DisplayName("read_daily")]
    [AgentTool]
    [Description("Read short-term daily memory.\nOmit date for today in the user's time zone.\nAn explicit date must be yyyy-MM-dd.")]
    public Task<string> ReadDailyMemoryAsync(
        [Description("Local date in yyyy-MM-dd format. Omit for today.")] string? date = null,
        CancellationToken ct = default) => ExecuteAsync("daily read", async () =>
    {
        if (date is null)
        {
            var today = await agentMemoryService.ReadDailyLogAsync(userId, ct: ct).ConfigureAwait(false);
            // Naming the date keeps the model from guessing it and walking back through older days.
            return today.Content ?? $"No daily memory saved for today ({today.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}).";
        }

        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var localDate))
        {
            throw new ToolFailureException("date must use yyyy-MM-dd.", "Supply a local date or omit date for today.");
        }

        return await agentMemoryService.ReadDailyLogForDateAsync(userId, localDate, ct).ConfigureAwait(false)
            ?? $"No daily memory saved for {date}.";
    }, ct);

    /// <summary>Appends temporary information to today's encrypted short-term log.</summary>
    [DisplayName("save_daily")]
    [AgentTool]
    [Description("Save temporary or short-term information in today's daily memory.\nAppends to the existing daily record in the user's time zone.\nFor durable user facts and preferences, use save.\nMaximum resulting daily document: 524288 UTF-8 bytes.")]
    public Task<string> SaveDailyMemoryAsync(
        [Description("Short-term information to append. The resulting complete daily document must fit 524288 UTF-8 bytes.")] string content,
        CancellationToken ct = default) => ExecuteAsync("daily save", async () =>
    {
        await agentMemoryService.AppendDailyLogAsync(userId, content, uiSettings.GetTimeZone(), ct).ConfigureAwait(false);
        return "Saved to today's short-term daily memory.";
    }, ct);

    /// <summary>Deletes the selected long-term concept, or every long-term concept, after explicit confirmation.</summary>
    [DisplayName("forget")]
    [AgentTool]
    [Description("Delete saved long-term user memory after the user confirms.\nSupply exactly one: conceptId for one concept, or scope all for every long-term concept.\nscope all keeps short-term daily memory.\nUse a conceptId returned by find, read or save.\nDo not overwrite memories to fulfill a forget request.\nLinked detail is retained. Remaining inbound links are reported.\nIf the user declines or cancels, stop. Do not retry.")]
    public Task<string> ForgetMemoryAsync(
        [Description("Existing long-term concept ID without .md. index, log and daily/ are reserved. Omit when scope is all.")] string? conceptId = null,
        [Description("all deletes every long-term concept. Omit when conceptId is supplied.")] string? scope = null,
        CancellationToken ct = default) => ExecuteAsync("long-term forget", async () =>
    {
        var store = agentMemoryService.GetStore(userId);
        if (DeletionConfirmation.RequireOneTarget(conceptId, scope, "all") is not null)
        {
            var count = store.ListConceptIds(string.Empty).Count(id => !IsDaily(id));
            if (count == 0) { return "No saved long-term concepts to delete."; }

            return await DeletionConfirmation.ConfirmAsync(runContextAccessor, elicitationRouter,
                $"Delete {DeletionConfirmation.Count(count, "saved user-memory concept", "saved user-memory concepts")}? Daily memory is kept.", ct).ConfigureAwait(false)
                ? await store.DeleteAllAsync(AgentMemoryService.DailyDirectoryName, snapshot: true, ct).ConfigureAwait(false)
                : "Deletion cancelled by the user. Every concept was retained. Do not retry unless the user asks again.";
        }

        var id = LongTermId(store, conceptId!);
        if (await store.ReadAsync(id, ct).ConfigureAwait(false) is null) { return $"Concept not found: {id}."; }

        return await DeletionConfirmation.ConfirmAsync(runContextAccessor, elicitationRouter,
            $"Delete saved user-memory concept '{id}'? Linked detail will be retained.", ct).ConfigureAwait(false)
            ? await store.DeleteAsync(id, ct).ConfigureAwait(false)
            : "Deletion cancelled by the user. The selected concept was retained. Do not retry unless the user asks again.";
    }, ct);

    private string CurrentAgentId() => runContextAccessor.Current?.CurrentAgentId
        ?? throw new ToolFailureException("An active agent run is required.");

    private static string LongTermId(EncryptedKnowledgeStore store, string conceptId)
    {
        var id = store.ResolveConceptId(conceptId);
        if (IsDaily(id))
        {
            throw new ToolFailureException("conceptId targets reserved daily memory.", "Use save_daily for short-term information.");
        }

        return id;
    }

    private static bool IsDaily(string id) =>
        id.Equals(AgentMemoryService.DailyDirectoryName, StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith(AgentMemoryService.DailyDirectoryName + "/", StringComparison.OrdinalIgnoreCase);

    private string FormatResults(IReadOnlyList<ChatMemorySearchResult> results, bool includeScore)
    {
        var builder = new StringBuilder();
        foreach (var result in results)
        {
            var localTime = result.TimestampUtc.ToUserTime(uiSettings.GetTimeZone());
            builder.AppendLine(result.Content)
                .Append($"[Local Time: {localTime.ToString("O", CultureInfo.InvariantCulture)}, Thread: {result.ThreadId}");
            if (includeScore && result.Score.HasValue) { builder.Append($", Score: {result.Score.Value:F3}"); }

            builder.AppendLine("]").AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private async Task<string> ExecuteAsync(string operation, Func<Task<string>> action, CancellationToken ct)
    {
        try { return await action().ConfigureAwait(false); }
        catch (ToolFailureException) { throw; }
        catch (AgentMemoryValidationException ex) { throw new ToolFailureException(ex.Message); }
        catch (LocalOnlyEmbeddingProviderBlockedException ex) { throw new ToolFailureException(ex.Message); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (UnauthorizedAccessException)
        {
            throw new ToolFailureException("The requested knowledge path is not authorized.", "Use a reference inside this user's memory bundle.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Memory operation {Operation} failed for user {UserId}", operation, userId);
            throw new ToolFailureException($"Memory {operation} is temporarily unavailable. Check the Assistant log.");
        }
    }
}
