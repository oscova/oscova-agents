using Oscova.Agents.Conversations;
using Oscova.Agents.Search;

namespace Oscova.Agents.Core.Conversation;

/// <summary>Shares conversation search documents between sidebar search and historical discovery.</summary>
public static class ChatHistorySearch
{
    /// <summary>Loads visible text for the supplied owned summaries without changing chat state.</summary>
    public static async Task<IReadOnlyList<LexicalSearchDocument>> ReadDocumentsAsync(
        IChatThreadRepository repository,
        string userId,
        IReadOnlyList<ChatThread> summaries,
        bool excludeIncognito,
        CancellationToken ct = default)
    {
        var documents = new List<LexicalSearchDocument>(summaries.Count);
        // Bulk history reads open the owned index once and omit unrelated model archives.
        var threads = (await repository.GetThreadsWithMessagesAsync(userId, ct).ConfigureAwait(false))
            .ToDictionary(thread => thread.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var summary in summaries)
        {
            ct.ThrowIfCancellationRequested();
            if (threads.TryGetValue(summary.Id, out var thread) && (!excludeIncognito || !thread.IsIncognito))
            {
                documents.Add(CreateDocument(thread));
            }
        }

        return documents;
    }

    /// <summary>Builds a search document from visible conversation text, excluding internal segments.</summary>
    public static LexicalSearchDocument CreateDocument(ChatThread thread) => new(
        thread.Id,
        thread.Title,
        string.Join("\n", thread.DisplayMessages.Select(ChatDisplayMessageFormatter.GetPrimaryText)));
}
