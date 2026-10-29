using Oscova.Agents.Conversations;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.Text;
using Oscova.Agents.Core.ApiContracts.Audit;
using Oscova.Agents.Core.ApiContracts.Memory;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Memory;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Utils;
using Oscova.Agents.Vector;
using System.Security.Cryptography;
using System.Text;

namespace Oscova.Agents.Infrastructure.Memory;

public sealed class ChatMemoryService : IChatMemoryService, IDisposable
{
    private const string CollectionName = "chat_memory";
    private const string ManifestFileName = "chat-memory-index.json.enc";
    private readonly Oscova.Agents.Core.Secrets.IVaultDataProtector _vault;
    private readonly string _userId;
    private readonly IUserDirectoryService _userDirectoryService;
    private readonly IChatThreadRepository _chatThreadRepository;
    private readonly IEmbeddingGeneratorFactory _generatorFactory;
    private readonly IEgressAuditStore _egressAuditStore;
    private readonly ModelProviderSettings _llmSettings;
    private readonly ILogger<ChatMemoryService> _logger;
    private readonly DurableVectorIndex _index;
    private readonly SemaphoreSlim _publicationGate = new(1, 1);

    private string GetVectorDbPath() => Path.Combine(_userDirectoryService.GetMemoryDirectoryPath(_userId), "vectors.db");

    public ChatMemoryService(
        string userId,
        IUserDirectoryService userDirectoryService,
        IChatThreadRepository chatThreadRepository,
        IEmbeddingGeneratorFactory generatorFactory,
        IEgressAuditStore egressAuditStore,
        ModelProviderSettings llmSettings,
        ILogger<ChatMemoryService> logger,
        Oscova.Agents.Core.Secrets.IVaultDataProtector vault)
    {
        _vault = vault;
        _userId = userId;
        _userDirectoryService = userDirectoryService;
        _chatThreadRepository = chatThreadRepository;
        _generatorFactory = generatorFactory;
        _egressAuditStore = egressAuditStore;
        _llmSettings = llmSettings;
        _logger = logger;
        string memoryDirectory = _userDirectoryService.GetMemoryDirectoryPath(userId);
        _index = EncryptedVectorStorage.CreateIndex(
            vault,
            userId,
            Path.Combine(memoryDirectory, "vectors.db"),
            Path.Combine(memoryDirectory, ManifestFileName),
            CollectionName,
            logger);
    }

    public async Task StoreThreadAsync(string userId, string agentId, string threadId, CancellationToken ct = default)
    {
        EnsureUser(userId);
        ct.ThrowIfCancellationRequested();
        var chatThread = await _chatThreadRepository.GetThreadAsync(userId, threadId, ct).ConfigureAwait(false);
        if (chatThread == null) { return; }

        int committedPosition = await GetCommittedPositionAsync(userId, threadId, ct).ConfigureAwait(false);
        if (committedPosition >= chatThread.DisplayMessages.Count)
        {
            return;
        }

        await StoreMessagesAsync(
            userId,
            agentId,
            threadId,
            [.. chatThread.DisplayMessages.Skip(committedPosition)],
            committedPosition,
            chatThread.DisplayMessages.Count,
            ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes all stored chat-memory chunks for one deleted thread so the vector store stays aligned with chat state.
    /// </summary>
    public async Task DeleteThreadAsync(string userId, string threadId, CancellationToken ct = default)
    {
        EnsureUser(userId);
        await _publicationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _index.DeleteSourceAsync(threadId, GetConfiguredDimensions(), ct).ConfigureAwait(false);
        }
        finally { _publicationGate.Release(); }
    }

    /// <inheritdoc />
    public async Task SealThreadAsync(
        string userId,
        string agentId,
        string threadId,
        CancellationToken ct = default)
    {
        EnsureUser(userId);
        DurableVectorSpace? space = GetVectorSpace();
        if (space is null)
        {
            await DeleteThreadAsync(userId, threadId, ct).ConfigureAwait(false);
            return;
        }

        // AppendAsync validates this non-Append marker under the same mutation gate.
        await _index.SetSourceStateAsync(
            space,
            threadId,
            generationId: "confidential",
            DurableVectorSourceState.Skipped,
            reason: "Confidential Chat enabled.",
            sourceMetadata: agentId,
            cancellationToken: ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ReleaseAgentSealsAsync(string userId, string agentId, CancellationToken ct = default)
    {
        EnsureUser(userId);
        DurableVectorIndexSnapshot snapshot = await _index.GetSnapshotAsync(ct).ConfigureAwait(false);
        foreach (var (sourceId, source) in snapshot.Sources)
        {
            if (source.Mode == DurableVectorSourceMode.Replace &&
                source.State == DurableVectorSourceState.Skipped &&
                string.Equals(source.Metadata, agentId, StringComparison.OrdinalIgnoreCase))
            {
                await _index.DeleteSourceAsync(sourceId, GetConfiguredDimensions(), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Removes all stored chat-memory chunks after the user's threads have been cleared.
    /// </summary>
    public async Task<int> DeleteAllThreadsAsync(string userId, CancellationToken ct = default)
    {
        EnsureUser(userId);
        await _publicationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await _index.DeleteAllSourcesAsync(GetConfiguredDimensions(), ct).ConfigureAwait(false);
        }
        finally { _publicationGate.Release(); }
    }

    /// <summary>
    /// Removes all stored chat-memory chunks for one deleted agent.
    /// </summary>
    public async Task<int> DeleteByAgentAsync(string userId, string agentId, CancellationToken ct = default)
    {
        EnsureUser(userId);
        await _publicationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await _index.DeleteByOwnerAsync(agentId, GetConfiguredDimensions(), ct).ConfigureAwait(false);
        }
        finally { _publicationGate.Release(); }
    }

    /// <inheritdoc />
    public async Task ResetIndexAsync(string userId, CancellationToken ct = default)
    {
        EnsureUser(userId);
        await _index.ResetAsync(GetVectorSpaceOrUnconfigured(), ct).ConfigureAwait(false);

        var threads = await _chatThreadRepository.GetThreadsAsync(userId, ct: ct).ConfigureAwait(false);
        foreach (var thread in threads)
        {
            await _chatThreadRepository.UpdateWatermarksAsync(
                userId,
                thread.Id,
                lastIndexedMessageCount: 0,
                lastSummarizedMessageCount: null,
                ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task<int> GetCommittedPositionAsync(
        string userId,
        string threadId,
        CancellationToken ct = default)
    {
        EnsureUser(userId);
        ct.ThrowIfCancellationRequested();
        DurableVectorSpace? space = GetVectorSpace();
        return space is null
            ? Task.FromResult(0)
            : _index.GetCommittedPositionAsync(space, threadId, ct);
    }

    /// <summary>
    /// Stores the supplied display messages so consolidation can index only new turns.
    /// </summary>
    public async Task<int?> StoreMessagesAsync(
            string userId,
            string agentId,
            string threadId,
            IReadOnlyList<ChatDisplayMessage> messages,
            int expectedPosition,
            int nextPosition,
            bool requireLocalEndpoint = false,
            CancellationToken ct = default)
        {
            EnsureUser(userId);
            ct.ThrowIfCancellationRequested();
            if (messages.Count == 0)
            {
                return null;
            }

            var chatContent = BuildConversationText(messages);
            if (string.IsNullOrWhiteSpace(chatContent))
            {
                return null;
            }

    #pragma warning disable SKEXP0050 // Type is for evaluation purposes only
            var lines = TextChunker.SplitPlainTextLines(chatContent, 100, tokenCounter: AgentTokenCounter.CountTokens);
            var chunks = TextChunker.SplitPlainTextParagraphs(lines, 400, 80, tokenCounter: AgentTokenCounter.CountTokens);
    #pragma warning restore SKEXP0050

            if (chunks.Count == 0)
            {
                return null;
            }

            DurableVectorSpace? space = GetVectorSpace();
            if (space is null)
            {
                return null;
            }

            int committedPosition = await _index.GetCommittedPositionAsync(
                space,
                threadId,
                ct).ConfigureAwait(false);
            if (committedPosition != expectedPosition)
            {
                return committedPosition;
            }

            var resolution = await _generatorFactory.CreateAsync(
                requireLocalEndpoint: requireLocalEndpoint,
                cancellationToken: ct).ConfigureAwait(false);
            using var generator = resolution.Generator;
            if (generator == null) { return null; }

            // Retain the admitted outbound attempt even when generation fails.
            await WriteEmbeddingEgressAsync(userId, EgressAuditKinds.SourceEmbeddingIndexing, threadId, ct).ConfigureAwait(false);
            var embeddings = await generator.GenerateAsync(chunks, cancellationToken: ct).ConfigureAwait(false);

            var recordsToSave = new List<DurableVectorChunk>();
            DateTimeOffset timestamp = DateTimeOffset.UtcNow;
            for (var i = 0; i < chunks.Count; i++)
            {
                recordsToSave.Add(new DurableVectorChunk(
                    chunks[i],
                    embeddings[i].Vector,
                    i,
                    Timestamp: timestamp));
            }

            string generationId = $"{expectedPosition}:{nextPosition}:" +
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(chatContent)));
            // Recheck after embedding, sharing only publication with deletion so indexing never blocks chat admission.
            await _publicationGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var current = await _chatThreadRepository.GetThreadAsync(userId, threadId, ct).ConfigureAwait(false);
                // An embedding started before regeneration must not republish the replaced answer.
                if (current?.DisplayMessages.Skip(expectedPosition).Take(messages.Count)
                    .Select(message => message.MessageId).SequenceEqual(messages.Select(message => message.MessageId)) != true)
                {
                    return null;
                }

                DurableVectorAppendResult result = await _index.AppendAsync(
                    space,
                    threadId,
                    agentId,
                    generationId,
                    expectedPosition,
                    nextPosition,
                    recordsToSave,
                    ct).ConfigureAwait(false);
                return result.CommittedPosition;
            }
            finally { _publicationGate.Release(); }
        }

    private static string BuildConversationText(IReadOnlyList<ChatDisplayMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var message in messages)
        {
            // Index the same assistant transcript the UI renders by flattening segments instead of relying on message.Text.
            var conversationLine = ChatDisplayMessageFormatter.GetConversationLine(message, includeNonTextSegments: true);
            if (string.IsNullOrWhiteSpace(conversationLine))
            {
                continue;
            }

            sb.AppendLine(conversationLine);
        }

        return sb.ToString();
    }

    private async Task WriteEmbeddingEgressAsync(string userId, string source, string? threadId, CancellationToken ct)
    {
        var info = _generatorFactory.DescribeEgress();
        if (info is not { IsCloud: true } egress)
        {
            return;
        }

        try
        {
            await _egressAuditStore.AppendAsync(userId, new EgressAuditRecord
            {
                Kind = EgressAuditKinds.Embedding,
                Source = source,
                ThreadId = threadId,
                ProviderId = egress.ProviderId,
                ModelId = egress.ModelId
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append embedding egress audit line for source {EgressSource}.", source);
        }
    }

    public async Task<SemanticSearchResult<ChatMemorySearchResult>> SearchAsync(
        string userId,
        string agentId,
        string query,
        int maxResults = 5,
        bool requireLocalEndpoint = false,
        CancellationToken ct = default)
    {
        // Tool-based recall spans the full per-user store because consolidation can persist
        // chunks under a different AgentId than the agent currently asking for memory.
        _ = agentId;
        EnsureUser(userId);
        ct.ThrowIfCancellationRequested();

        DurableVectorSpace? space = GetVectorSpace();
        if (space is null)
        {
            return new([], "Past conversations cannot be searched until an embedding provider is added in Settings > Agents & AI > Model Providers > Add embedding provider. Daily and long-term memory work without it.");
        }

        // An empty index can mean indexing is paused, so report provider availability first.
        var resolution = await _generatorFactory.CreateAsync(
            requireLocalEndpoint: requireLocalEndpoint, cancellationToken: ct).ConfigureAwait(false);
        using var generator = resolution.Generator;
        if (generator is null) { return new([], resolution.UnavailableReason); }

        DurableVectorIndexSnapshot snapshot = await _index.GetSnapshotAsync(ct).ConfigureAwait(false);
        if (!IsCurrent(snapshot, space) ||
            snapshot.Sources.Values.All(static source => source.CommittedPosition == 0))
        {
            return new([]);
        }

        // Recall queries can leave the host before the provider reports a failure.
        await WriteEmbeddingEgressAsync(userId, EgressAuditKinds.SourceEmbeddingSearch, threadId: null, ct).ConfigureAwait(false);
        var queryEmbedding = await generator.GenerateAsync(query, cancellationToken: ct).ConfigureAwait(false);

        IReadOnlyList<DurableVectorHit> searchResults = await _index.SearchAsync(
            space,
            queryEmbedding,
            maxResults,
            ct).ConfigureAwait(false);
        return new(searchResults
            .Select(static result => new ChatMemorySearchResult
            {
                Content = result.Content,
                ThreadId = result.SourceId,
                TimestampUtc = result.Timestamp,
                Score = result.Score
            })
            .ToList());
    }

    public async Task<IReadOnlyList<ChatMemorySearchResult>> GetByThreadIdAsync(string userId, string agentId, string threadId, CancellationToken ct = default)
    {
        // Thread reads also ignore AgentId so the current agent can recall memories indexed during consolidation.
        _ = agentId;
        EnsureUser(userId);
        ct.ThrowIfCancellationRequested();

        DurableVectorSpace? space = GetVectorSpace();
        if (space is null)
        {
            return [];
        }

        IReadOnlyList<DurableVectorHit> records = await _index.BrowseAsync(
            space,
            ownerId: null,
            threadId,
            cancellationToken: ct).ConfigureAwait(false);
        return records
            .OrderBy(static record => record.Timestamp)
            .ThenBy(static record => record.ChunkIndex)
            .Select(static record => new ChatMemorySearchResult
            {
                Content = record.Content,
                ThreadId = record.SourceId,
                TimestampUtc = record.Timestamp
            })
            .ToList();
    }

    /// <summary>
    /// Reads one page of stored chat-memory chunks for the inspector dialog.
    /// </summary>
    public async Task<ChatMemoryPage> GetPageAsync(
        string userId,
        string? agentId,
        string? threadId,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        var normalizedPage = Math.Max(1, page);
        var normalizedPageSize = Math.Clamp(pageSize, 1, 200);
        var requestedSkip = ((long)normalizedPage - 1) * normalizedPageSize;
        if (requestedSkip > int.MaxValue)
        {
            _logger.LogWarning(
                "Chat memory page request exceeded the vector-store skip range for user '{UserId}'. Page {Page}, page size {PageSize}, requested skip {RequestedSkip}.",
                userId,
                normalizedPage,
                normalizedPageSize,
                requestedSkip);

            return BuildPage([], normalizedPage, hasNextPage: false);
        }

        var skip = (int)requestedSkip;
        EnsureUser(userId);
        ct.ThrowIfCancellationRequested();
        DurableVectorSpace? space = GetVectorSpace();
        if (space is null || !File.Exists(GetVectorDbPath()))
        {
            return BuildPage([], normalizedPage, hasNextPage: false);
        }

        IReadOnlyList<DurableVectorHit> records = await _index.BrowseAsync(
            space,
            agentId,
            threadId,
            skip,
            normalizedPageSize + 1,
            ct).ConfigureAwait(false);
        List<DurableVectorHit> pageRecords = [.. records];
        bool hasNextPage = pageRecords.Count > normalizedPageSize;
        if (hasNextPage)
        {
            pageRecords.RemoveAt(pageRecords.Count - 1);
        }

        return BuildPage(pageRecords, normalizedPage, hasNextPage);
    }

    /// <summary>
    /// Shapes raw record results into the UI paging contract consumed by the dialog.
    /// </summary>
    private static ChatMemoryPage BuildPage(
        IReadOnlyList<DurableVectorHit> records,
        int page,
        bool hasNextPage)
    {
        return new ChatMemoryPage
        {
            Entries =
            [
                .. records.Select(record => new ChatMemoryEntry
                {
                    Key = record.Key,
                    AgentId = record.OwnerId ?? string.Empty,
                    ThreadId = record.SourceId,
                    Content = record.Content,
                    TimestampUtc = record.Timestamp.UtcDateTime,
                    ChunkIndex = record.ChunkIndex
                })
            ],
            CurrentPage = page,
            HasNextPage = hasNextPage
        };
    }

    private DurableVectorSpace? GetVectorSpace() =>
        EmbeddingGeneratorFactory.CreateVectorSpace(
            _llmSettings.SelectedEmbeddingModelProvider);

    private DurableVectorSpace GetVectorSpaceOrUnconfigured() =>
        GetVectorSpace() ?? new DurableVectorSpace("unconfigured", GetConfiguredDimensions());

    private int GetConfiguredDimensions() =>
        Math.Max(1, _llmSettings.SelectedEmbeddingModelProvider?.VectorDimensions ?? 768);

    private static bool IsCurrent(
        DurableVectorIndexSnapshot snapshot,
        DurableVectorSpace space) =>
        snapshot.IsTrusted &&
        string.Equals(snapshot.Fingerprint, space.Fingerprint, StringComparison.Ordinal) &&
        snapshot.VectorDimensions == space.VectorDimensions;

    private void EnsureUser(string userId)
    {
        if (!string.Equals(userId, _userId, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Chat memory belongs to another user scope.");
        }

        EncryptedVectorStorage.EnsureUnlocked(_vault, userId);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _index.Dispose();
        _publicationGate.Dispose();
    }
}
