using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Utils;
using Oscova.Agents.Persistence;
using Oscova.Agents.Secrets;

namespace Oscova.Agents.Conversations;

/// <summary>Persists conversation threads beneath a caller-selected users root.</summary>
public sealed class ChatThreadRepository(
    Func<string> usersRootResolver,
    FileSessionIndexCoordinator indexCoordinator,
    IRedactionService redactionService,
    IContentProtector contentProtector,
    ILogger<ChatThreadRepository> logger) : IChatThreadRepository
{
    private const string SessionIndexFile = "index.jsonl";
    private const string SessionTranscriptExtension = ".jsonl";
    private const string SessionFileSuffix = ".session.json.enc";
    private const string IndexPurpose = "chat\0index";

    private const string ChatDirectory = "chat";
    private const string HeaderKind = "header";
    private const string MessageKind = "message";

    private readonly Func<string> _usersRootResolver = usersRootResolver
        ?? throw new ArgumentNullException(nameof(usersRootResolver));

    // Incognito threads live here for the life of the process and never reach disk.
    private readonly ConcurrentDictionary<(string UserId, string ThreadId), ChatThread> _incognitoThreads =
        new(IncognitoKeyComparer.Instance);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    // Purposes bind ciphertext to its owner and artifact and are authenticated inside the payload,
    // so a write purpose and its read purpose must match byte for byte.


    private static string TranscriptPurpose(string threadId) => $"chat\0{threadId}\0transcript";
    private static string SessionPurpose(string threadId) => $"chat\0{threadId}\0session";

    /// <inheritdoc />
    public Task<IReadOnlyList<ChatThread>> GetThreadsAsync(string userId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<ChatThread> threads = ReadIndex(userId)
            .OrderByDescending(row => row.UpdatedAt)
            .Select(row => MapSummary(row, userId))
            .ToList();
        return Task.FromResult(threads);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ChatThread>> GetThreadsWithMessagesAsync(string userId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var threads = new List<ChatThread>();
        // Open the owned index once instead of reopening it for each transcript.
        foreach (var row in ReadIndex(userId).OrderByDescending(row => row.UpdatedAt))
        {
            ct.ThrowIfCancellationRequested();
            var thread = MapSummary(row, userId);
            thread.DisplayMessages = ReadTranscript(userId, row.SessionId).Messages;
            threads.Add(thread);
        }
        return Task.FromResult<IReadOnlyList<ChatThread>>(threads);
    }

    /// <inheritdoc />
    public Task<ChatThread?> GetThreadAsync(string userId, string threadId, CancellationToken ct = default,
        int? messageLimit = null, string? beforeMessageId = null, string? firstMessageId = null)
    {
        ct.ThrowIfCancellationRequested();
        ValidateThreadId(userId, threadId);
        if (messageLimit is <= 0 || (beforeMessageId is not null && firstMessageId is not null) ||
            (messageLimit is null && (beforeMessageId is not null || firstMessageId is not null)))
        {
            throw new ArgumentException("Use a positive messageLimit and only one history cursor.");
        }
        if (!contentProtector.IsAvailable(userId)) { return Task.FromResult<ChatThread?>(null); }
        if (_incognitoThreads.TryGetValue((userId, threadId), out var incognitoThread))
        {
            if (messageLimit is int limit)
            {
                // A display page must not remove messages from the live in-memory conversation.
                var end = beforeMessageId is null ? incognitoThread.DisplayMessages.Count :
                    incognitoThread.DisplayMessages.ToList().FindIndex(message => message.MessageId == beforeMessageId);
                var start = firstMessageId is null ? Math.Max(0, end - limit) :
                    incognitoThread.DisplayMessages.ToList().FindIndex(message => message.MessageId == firstMessageId);
                if (end < 0 || start < 0) { throw new ArgumentException("The history cursor no longer exists. Reopen the conversation."); }
                while (start > 0 && (incognitoThread.DisplayMessages[start].From != MessageOwner.User ||
                    incognitoThread.DisplayMessages[start].IsInteractionResponse)) { start--; }
                return Task.FromResult<ChatThread?>(new ChatThread
                {
                    Id = incognitoThread.Id, Title = incognitoThread.Title, AgentId = incognitoThread.AgentId,
                    AgentName = incognitoThread.AgentName, ProjectId = incognitoThread.ProjectId,
                    IsIncognito = true, IsCurrent = incognitoThread.IsCurrent, IsUnread = incognitoThread.IsUnread,
                    UpdatedAt = incognitoThread.UpdatedAt, SystemInstruction = incognitoThread.SystemInstruction,
                    SessionElement = incognitoThread.SessionElement, BranchOrigin = incognitoThread.BranchOrigin,
                    SourceThreadAvailable = incognitoThread.SourceThreadAvailable,
                    BranchableMessageIds = incognitoThread.BranchableMessageIds,
                    DisplayMessages = incognitoThread.DisplayMessages.Skip(start).Take(end - start).ToList(),
                    HasOlderDisplayMessages = start > 0
                });
            }
            // Callers mutate the thread and save it back, so hand out the live instance rather than a copy.
            return Task.FromResult<ChatThread?>(incognitoThread);
        }

        var row = TryReadIndexEntry(userId, threadId);
        if (row is null)
        {
            return Task.FromResult<ChatThread?>(null);
        }


        var thread = MapSummary(row, userId);
        if (messageLimit is int pageLimit)
        {
            ReadTranscriptPage(userId, row.SessionId, thread, pageLimit, beforeMessageId, firstMessageId);
        }
        else
        {
            var (messages, systemInstruction) = ReadTranscript(userId, row.SessionId);
            thread.DisplayMessages = messages;
            thread.SystemInstruction = systemInstruction;
        }
        var archive = ReadSessionArchive(userId, row.SessionId);
        thread.SessionElement = archive?.SessionElement;
        SetCheckpoints(thread, archive?.Checkpoints ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal));

        return Task.FromResult<ChatThread?>(thread);
    }

    /// <inheritdoc />
    public Task<bool> ThreadExistsAsync(string userId, string threadId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ValidateThreadId(userId, threadId);
        return Task.FromResult(_incognitoThreads.ContainsKey((userId, threadId)) || TryReadIndexEntry(userId, threadId) is not null);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetProjectThreadIdsAsync(string userId, string projectId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var persisted = ReadIndex(userId).Where(row => row.ProjectId == projectId).Select(row => row.SessionId);
        var ephemeral = _incognitoThreads.Where(pair => pair.Key.UserId == userId && pair.Value.ProjectId == projectId)
            .Select(pair => pair.Key.ThreadId);
        return Task.FromResult<IReadOnlyList<string>>(persisted.Concat(ephemeral).Distinct(StringComparer.Ordinal).ToArray());
    }

    /// <inheritdoc />
    /// <inheritdoc />
    public Task CreateThreadAsync(string userId, ChatThread thread, bool setCurrent = true, CancellationToken ct = default)
        => SaveThreadCoreAsync(userId, thread, setCurrent, create: true, ct);

    /// <inheritdoc />
    public Task SaveThreadAsync(string userId, ChatThread thread, bool setCurrent = true, CancellationToken ct = default)
        => SaveThreadCoreAsync(userId, thread, setCurrent, create: false, ct);

    private async Task SaveThreadCoreAsync(
        string userId,
        ChatThread thread,
        bool setCurrent,
        bool create,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ValidateThreadId(userId, thread.Id);
        if (!contentProtector.IsAvailable(userId))
        {
            throw new InvalidOperationException("Unlock your Vault Password before continuing.");
        }
        if (thread.IsIncognito)
        {
            _incognitoThreads.TryGetValue((userId, thread.Id), out var savedIncognito);
            SetCheckpoints(thread, thread.SessionCheckpoints ?? savedIncognito?.SessionCheckpoints ??
                new Dictionary<string, JsonElement>(StringComparer.Ordinal));
            thread.IsCurrent = false;
            thread.UpdatedAt = DateTimeOffset.UtcNow;
            if (create)
            {
                _incognitoThreads[(userId, thread.Id)] = thread;
            }
            else if (_incognitoThreads.TryGetValue((userId, thread.Id), out var existingIncognito))
            {
                _incognitoThreads.TryUpdate((userId, thread.Id), thread, existingIncognito);
            }
            return;
        }

        // A thread saved without the flag must not stay behind in memory under the same id.
        _incognitoThreads.TryRemove((userId, thread.Id), out _);

        await indexCoordinator.RunForUserAsync(userId, async cancellationToken =>
        {
            var existing = TryReadIndexEntry(userId, thread.Id);
            if (existing is null && !create)
            {
                // Authoritative deletion must win over a run that finishes later.
                return;
            }

            thread.Id = existing?.SessionId ?? thread.Id;
            // Refuse an unsupported native archive before an ordinary save changes its display history.
            _ = ReadSessionArchive(userId, thread.Id);
            await AppendNewTranscriptMessagesAsync(userId, thread, existing, cancellationToken).ConfigureAwait(false);
            await WriteSessionAsync(userId, thread, cancellationToken).ConfigureAwait(false);

            var rows = ReadIndex(userId);
            UpsertIndexRow(rows, thread, existing, setCurrent, userId);
            await WriteIndexAsync(userId, rows, cancellationToken).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> TryMarkThreadReadAsync(
        string userId,
        string threadId,
        CancellationToken ct = default)
    {
        ValidateThreadId(userId, threadId);
        if (_incognitoThreads.TryGetValue((userId, threadId), out var incognitoThread))
        {
            incognitoThread.IsUnread = false;
            return true;
        }

        var marked = false;
        await indexCoordinator.RunForUserAsync(userId, async cancellationToken =>
        {
            var rows = ReadIndex(userId);
            var row = rows.FirstOrDefault(entry =>
                string.Equals(entry.SessionId, threadId, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                // Read receipts may update existing metadata, but must not restore deleted conversations.
                return;
            }

            row.IsUnread = false;
            await WriteIndexAsync(userId, rows, cancellationToken).ConfigureAwait(false);
            marked = true;
        }, ct).ConfigureAwait(false);

        return marked;
    }

    /// <inheritdoc />
    public async Task<bool> TryRenameThreadAsync(
        string userId,
        string threadId,
        string title,
        CancellationToken ct = default)
    {
        ValidateThreadId(userId, threadId);
        if (_incognitoThreads.TryGetValue((userId, threadId), out var incognitoThread))
        {
            incognitoThread.Title = title;
            incognitoThread.UpdatedAt = DateTimeOffset.UtcNow;
            return true;
        }

        var renamed = false;
        await indexCoordinator.RunForUserAsync(userId, async cancellationToken =>
        {
            var rows = ReadIndex(userId);
            var row = rows.FirstOrDefault(entry =>
                string.Equals(entry.SessionId, threadId, StringComparison.OrdinalIgnoreCase));
            if (row is null || !contentProtector.IsAvailable(userId))
            {
                // Metadata updates may only mutate an existing readable record so deletion remains final.
                return;
            }

            row.Title = title;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await WriteIndexAsync(userId, rows, cancellationToken).ConfigureAwait(false);
            renamed = true;
        }, ct).ConfigureAwait(false);

        return renamed;
    }

    /// <inheritdoc />
    public async Task<bool> TryReplaceDisplayMessageAsync(string userId, string threadId,
        ChatDisplayMessage message, CancellationToken ct = default)
    {
        ValidateThreadId(userId, threadId);
        if (_incognitoThreads.TryGetValue((userId, threadId), out var incognitoThread))
        {
            lock (incognitoThread.DisplayMessages)
            {
                var existing = incognitoThread.DisplayMessages.FirstOrDefault(item => item.MessageId == message.MessageId);
                if (existing is null) return false;
                incognitoThread.DisplayMessages[incognitoThread.DisplayMessages.IndexOf(existing)] = message;
                return true;
            }
        }

        var replaced = false;
        await indexCoordinator.RunForUserAsync(userId, async cancellationToken =>
        {
            // Reload under the persistence gate so a deleted thread cannot be restored.
            var thread = await GetThreadAsync(userId, threadId, cancellationToken).ConfigureAwait(false);
            var existing = thread?.DisplayMessages.FirstOrDefault(item => item.MessageId == message.MessageId);
            if (thread is null || existing is null) return;
            thread.DisplayMessages[thread.DisplayMessages.IndexOf(existing)] = message;
            await RewriteTranscriptAsync(userId, thread, TryReadIndexEntry(userId, threadId), cancellationToken).ConfigureAwait(false);
            replaced = true;
        }, ct).ConfigureAwait(false);
        return replaced;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ChatThread>> GetIncognitoThreadsAsync(string userId, CancellationToken ct = default)
    {
        IReadOnlyList<ChatThread> threads =
        [
            .. _incognitoThreads
                .Where(pair => string.Equals(pair.Key.UserId, userId, StringComparison.Ordinal))
                .Select(static pair => pair.Value)
                .OrderByDescending(static thread => thread.UpdatedAt)
        ];

        return Task.FromResult(threads);
    }

    /// <inheritdoc />
    public Task<JsonElement?> GetSessionElementAsync(string userId, string threadId, CancellationToken ct = default)
    {
        _ = ct;
        ValidateThreadId(userId, threadId);
        if (_incognitoThreads.TryGetValue((userId, threadId), out var incognitoThread))
        {
            return Task.FromResult(incognitoThread.SessionElement);
        }

        var storedThreadId = TryReadIndexEntry(userId, threadId)?.SessionId ?? threadId;
        return Task.FromResult(ReadSessionElement(userId, storedThreadId));
    }

    /// <inheritdoc />
    public async Task SaveSessionElementAsync(
            string userId,
            string threadId,
            JsonElement sessionElement,
            string? agentId,
            CancellationToken ct = default)
    {
        ValidateThreadId(userId, threadId);
        if (_incognitoThreads.TryGetValue((userId, threadId), out var incognitoThread))
        {
            incognitoThread.SessionElement = sessionElement;
            incognitoThread.UpdatedAt = DateTimeOffset.UtcNow;
            return;
        }

        await indexCoordinator.RunForUserAsync(userId, async cancellationToken =>
        {
            var rows = ReadIndex(userId);
            var row = rows.FirstOrDefault(entry => string.Equals(entry.SessionId, threadId, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                // Session finalization may update only a thread that authoritative cleanup still owns.
                return;
            }

            var storedThreadId = row.SessionId;
            var archive = ReadSessionArchive(userId, storedThreadId) ?? new SessionArchive();
            archive.SessionElement = sessionElement.Clone();
            await WriteSessionArchiveAsync(userId, storedThreadId, archive, cancellationToken).ConfigureAwait(false);

            row.AgentId ??= agentId;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await WriteIndexAsync(userId, rows, cancellationToken).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteSessionElementAsync(string userId, string threadId, CancellationToken ct = default)
    {
        ValidateThreadId(userId, threadId);
        if (_incognitoThreads.TryGetValue((userId, threadId), out var incognitoThread))
        {
            incognitoThread.SessionElement = null;
            SetCheckpoints(incognitoThread, new Dictionary<string, JsonElement>(StringComparer.Ordinal));
            return;
        }

        await indexCoordinator.RunForUserAsync(userId, _ =>
        {
            var storedThreadId = TryReadIndexEntry(userId, threadId)?.SessionId ?? threadId;
            File.Delete(GetSessionPath(userId, storedThreadId));
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdateWatermarksAsync(
        string userId,
        string threadId,
        int? lastIndexedMessageCount,
        int? lastSummarizedMessageCount,
        CancellationToken ct = default,
        int? lastReflectedMessageCount = null,
        int? lastSummarizedMessageOffset = null,
        int? lastReflectedMessageOffset = null)
    {
        ValidateThreadId(userId, threadId);
        if (_incognitoThreads.ContainsKey((userId, threadId)))
        {
            return;
        }

        await indexCoordinator.RunForUserAsync(userId, async cancellationToken =>
        {
            var rows = ReadIndex(userId);
            var row = rows.FirstOrDefault(entry => string.Equals(entry.SessionId, threadId, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                return;
            }

            if (lastIndexedMessageCount.HasValue)
            {
                row.LastIndexedMessageCount = lastIndexedMessageCount.Value;
            }

            if (lastSummarizedMessageCount.HasValue)
            {
                (row.LastSummarizedMessageCount, row.LastSummarizedMessageOffset) = FurthestProgress(
                    row.LastSummarizedMessageCount, row.LastSummarizedMessageOffset,
                    lastSummarizedMessageCount.Value, lastSummarizedMessageOffset ?? 0);
            }

            if (lastReflectedMessageCount.HasValue)
            {
                (row.LastReflectedMessageCount, row.LastReflectedMessageOffset) = FurthestProgress(
                    row.LastReflectedMessageCount, row.LastReflectedMessageOffset,
                    lastReflectedMessageCount.Value, lastReflectedMessageOffset ?? 0);
            }

            // Background progress keeps UpdatedAt so the thread list stays ordered by conversation activity.
            await WriteIndexAsync(userId, rows, cancellationToken).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task MarkCurrentAsync(string userId, string agentId, string threadId, CancellationToken ct = default)
    {
        ValidateThreadId(userId, threadId);
        if (_incognitoThreads.ContainsKey((userId, threadId)))
        {
            return;
        }

        await indexCoordinator.RunForUserAsync(userId, async cancellationToken =>
        {
            var rows = ReadIndex(userId);
            var selected = rows.FirstOrDefault(row => string.Equals(row.SessionId, threadId, StringComparison.OrdinalIgnoreCase));
            if (selected is null) { return; }
            foreach (var row in rows.Where(row => string.Equals(row.AgentId, agentId, StringComparison.Ordinal) &&
                         string.Equals(row.ProjectId, selected.ProjectId, StringComparison.Ordinal)))
            {
                row.IsCurrent = string.Equals(row.SessionId, threadId, StringComparison.OrdinalIgnoreCase);
            }

            await WriteIndexAsync(userId, rows, cancellationToken).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ClearCurrentAsync(string userId, string agentId, string? projectId, CancellationToken ct = default)
    {
        await indexCoordinator.RunForUserAsync(userId, async cancellationToken =>
        {
            var rows = ReadIndex(userId);
            var changed = false;
            foreach (var row in rows.Where(row => string.Equals(row.AgentId, agentId, StringComparison.Ordinal) &&
                         string.Equals(row.ProjectId, projectId, StringComparison.Ordinal) && row.IsCurrent))
            {
                row.IsCurrent = false;
                changed = true;
            }

            if (changed)
            {
                await WriteIndexAsync(userId, rows, cancellationToken).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteThreadAsync(string userId, string threadId, CancellationToken ct = default)
    {
        ValidateThreadId(userId, threadId);
        _incognitoThreads.TryRemove((userId, threadId), out _);

        await indexCoordinator.RunForUserAsync(userId, async cancellationToken =>
        {
            var rows = ReadIndex(userId);
            var row = rows.FirstOrDefault(entry => string.Equals(entry.SessionId, threadId, StringComparison.OrdinalIgnoreCase));
            var storedThreadId = row?.SessionId ?? threadId;
            var transcriptPath = GetTranscriptPath(userId, storedThreadId);
            var sessionPath = GetSessionPath(userId, storedThreadId);
            if (Path.Exists(transcriptPath)) File.Delete(transcriptPath);
            if (Path.Exists(sessionPath)) File.Delete(sessionPath);
            var removed = rows.RemoveAll(entry => string.Equals(entry.SessionId, threadId, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                await WriteIndexAsync(userId, rows, cancellationToken).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> DeleteAllThreadsAsync(string userId, CancellationToken ct = default)
    {
        var count = RemoveIncognitoThreads(userId, static _ => true);
        await indexCoordinator.RunForUserAsync(userId, cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = GetChatDirectory(userId);
            if (!Directory.Exists(directory)) { return Task.CompletedTask; }
            var threadIds = Directory.GetFiles(directory, "*" + SessionTranscriptExtension)
                .Where(path => !string.Equals(Path.GetFileName(path), SessionIndexFile, StringComparison.Ordinal))
                .Select(path => Path.GetFileName(path)[..^SessionTranscriptExtension.Length])
                .ToArray();
            foreach (var threadId in threadIds) { ValidateThreadId(userId, threadId); }
            foreach (var threadId in threadIds)
            {
                File.Delete(GetTranscriptPath(userId, threadId));
                File.Delete(GetSessionPath(userId, threadId));
            }
            File.Delete(GetIndexPath(userId));
            count += threadIds.Length;
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);
        return count;
    }

    /// <inheritdoc />
    public async Task<int> DeleteThreadsByAgentAsync(string userId, string agentId, CancellationToken ct = default)
    {
        var incognitoCount = RemoveIncognitoThreads(
            userId,
            thread => string.Equals(thread.AgentId, agentId, StringComparison.Ordinal));

        List<ChatSessionIndexEntry> removedRows = [];
        await indexCoordinator.RunForUserAsync(userId, async cancellationToken =>
        {
            var rows = ReadIndex(userId);
            removedRows = [.. rows.Where(row => string.Equals(row.AgentId, agentId, StringComparison.Ordinal))];
            if (removedRows.Count == 0)
            {
                return;
            }

            foreach (var row in removedRows)
            {
                ValidateThreadId(userId, row.SessionId);
            }

            rows.RemoveAll(row => string.Equals(row.AgentId, agentId, StringComparison.Ordinal));
            await WriteIndexAsync(userId, rows, cancellationToken).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        foreach (var row in removedRows)
        {
            TryDelete(GetTranscriptPath(userId, row.SessionId));
            TryDelete(GetSessionPath(userId, row.SessionId));
        }

        return removedRows.Count + incognitoCount;
    }

    private int RemoveIncognitoThreads(string userId, Func<ChatThread, bool> predicate)
    {
        var keys = _incognitoThreads
            .Where(pair => string.Equals(pair.Key.UserId, userId, StringComparison.Ordinal) && predicate(pair.Value))
            .Select(static pair => pair.Key)
            .ToList();

        var removed = 0;
        foreach (var key in keys)
        {
            if (_incognitoThreads.TryRemove(key, out _))
            {
                removed++;
            }
        }

        return removed;
    }



    private string SerializeRecord(ChatTranscriptRecord record, string userId, string threadId)
    {
        return contentProtector.Protect(userId, TranscriptPurpose(threadId), JsonSerializer.Serialize(record, JsonOptions));
    }







    private async Task AppendNewTranscriptMessagesAsync(
        string userId,
        ChatThread thread,
        ChatSessionIndexEntry? existing,
        CancellationToken ct)
    {
        var transcriptPath = GetTranscriptPath(userId, thread.Id);
        var persistedIds = ReadTranscriptMessageIds(userId, thread.Id, transcriptPath);
        var existingCount = persistedIds.Count;
        if (existingCount > thread.DisplayMessages.Count)
        {
            logger.LogWarning(
                "Thread {ThreadId} for user {UserId} has fewer supplied messages than persisted transcript rows. Rewriting transcript.",
                thread.Id,
                userId);
            await RewriteTranscriptAsync(userId, thread, existing, ct).ConfigureAwait(false);
            return;
        }

        // A message inserted before saved rows shifts them, so appending by count would save a row twice.
        if (!persistedIds.SequenceEqual(thread.DisplayMessages.Take(existingCount).Select(message => message.MessageId), StringComparer.Ordinal))
        {
            await RewriteTranscriptAsync(userId, thread, existing, ct).ConfigureAwait(false);
            return;
        }

        if (!File.Exists(transcriptPath))
        {
            await AppendHeaderAsync(userId, thread, existing, ct).ConfigureAwait(false);
        }
        else
        {
            await UpdateHeaderAsync(userId, thread, existing, ct).ConfigureAwait(false);
        }

        if (existingCount == thread.DisplayMessages.Count)
        {
            return;
        }

        for (var index = existingCount; index < thread.DisplayMessages.Count; index++)
        {
            var message = thread.DisplayMessages[index];
            BackfillBotAgentId(thread, message);
            var redacted = await RedactDisplayMessageAsync(userId, message, ct).ConfigureAwait(false);
            var record = new ChatTranscriptRecord
            {
                Kind = MessageKind,
                SessionId = thread.Id,
                Channel = string.IsNullOrWhiteSpace(redacted.Channel) ? ResolveThreadChannel(thread, existing) : redacted.Channel,
                Message = redacted,
                At = redacted.CreatedAt
            };
            await FileUtil.AppendLineSharedAsync(
                transcriptPath,
                SerializeRecord(record, userId, thread.Id),
                ct).ConfigureAwait(false);
        }
    }

    private async Task RewriteTranscriptAsync(
        string userId,
        ChatThread thread,
        ChatSessionIndexEntry? existing,
        CancellationToken ct)
    {
        var transcriptPath = GetTranscriptPath(userId, thread.Id);
        var lines = new List<string>
        {
            SerializeRecord(
                await CreateHeaderRecordAsync(userId, thread, existing, ct).ConfigureAwait(false),
                userId,
                thread.Id)
        };

        foreach (var message in thread.DisplayMessages)
        {
            BackfillBotAgentId(thread, message);
            var redacted = await RedactDisplayMessageAsync(userId, message, ct).ConfigureAwait(false);
            lines.Add(SerializeRecord(new ChatTranscriptRecord
            {
                Kind = MessageKind,
                SessionId = thread.Id,
                Channel = string.IsNullOrWhiteSpace(redacted.Channel) ? ResolveThreadChannel(thread, existing) : redacted.Channel,
                Message = redacted,
                At = redacted.CreatedAt
            }, userId, thread.Id));
        }

        await FileUtil.WriteAllTextAtomicAsync(transcriptPath, string.Join(Environment.NewLine, lines) + Environment.NewLine, ct).ConfigureAwait(false);
    }

    private static void BackfillBotAgentId(ChatThread thread, ChatDisplayMessage message)
    {
        // Persist the producer before a later agent switch changes the thread-level default.
        if (message.From == MessageOwner.Bot && string.IsNullOrWhiteSpace(message.AgentId))
        {
            message.AgentId = thread.AgentId;
        }
    }

    private async Task AppendHeaderAsync(
        string userId,
        ChatThread thread,
        ChatSessionIndexEntry? existing,
        CancellationToken ct)
    {
        var header = await CreateHeaderRecordAsync(userId, thread, existing, ct).ConfigureAwait(false);
        await FileUtil.AppendLineSharedAsync(
            GetTranscriptPath(userId, thread.Id),
            SerializeRecord(header, userId, thread.Id),
            ct).ConfigureAwait(false);
    }

    private async Task UpdateHeaderAsync(
        string userId,
        ChatThread thread,
        ChatSessionIndexEntry? existing,
        CancellationToken ct)
    {
        var transcriptPath = GetTranscriptPath(userId, thread.Id);
        var lines = FileUtil.ReadAllLinesShared(transcriptPath).ToList();
        var header = await CreateHeaderRecordAsync(userId, thread, existing, ct).ConfigureAwait(false);
        var headerIndex = FindHeaderLine(userId, thread.Id, lines, transcriptPath);

        if (headerIndex is not null)
        {
            var stored = DeserializeTranscriptRecord(contentProtector.Unprotect(userId, TranscriptPurpose(thread.Id), lines[headerIndex.Value])
                    ?? throw new InvalidOperationException("The encrypted transcript cannot be read."));
            if (stored is not null && string.Equals(
                    JsonSerializer.Serialize(stored, JsonOptions),
                    JsonSerializer.Serialize(header, JsonOptions),
                    StringComparison.Ordinal))
            {
                return;
            }
        }

        var serializedHeader = SerializeRecord(header, userId, thread.Id);

        if (headerIndex is null)
        {
            lines.Insert(0, serializedHeader);
            await WriteTranscriptLinesAsync(transcriptPath, lines, ct).ConfigureAwait(false);
            return;
        }

        lines[headerIndex.Value] = serializedHeader;
        await WriteTranscriptLinesAsync(transcriptPath, lines, ct).ConfigureAwait(false);
    }

    private async Task<ChatTranscriptRecord> CreateHeaderRecordAsync(
            string userId,
            ChatThread thread,
            ChatSessionIndexEntry? existing,
            CancellationToken ct)
    {
        return new ChatTranscriptRecord
        {
            Kind = HeaderKind,
            SessionId = thread.Id,
            Channel = ResolveThreadChannel(thread, existing),
            AgentId = thread.AgentId,
            BranchOrigin = thread.BranchOrigin,
            CreatedAt = existing?.CreatedAt ?? DateTimeOffset.UtcNow,
            SystemInstruction = string.IsNullOrWhiteSpace(thread.SystemInstruction)
                ? null
                : await redactionService.RedactTextAsync(thread.SystemInstruction, userId, ct).ConfigureAwait(false)
        };
    }


    private async Task WriteSessionAsync(string userId, ChatThread thread, CancellationToken ct)
    {
        var archive = ReadSessionArchive(userId, thread.Id) ?? new SessionArchive();
        // A supplied server snapshot replaces the retained set, including canceled completion cleanup.
        SetCheckpoints(thread, thread.SessionCheckpoints ?? archive.Checkpoints);
        archive.Checkpoints = thread.SessionCheckpoints!.ToDictionary(pair => pair.Key, pair => pair.Value,
            StringComparer.Ordinal);
        archive.SessionElement = thread.SessionElement;
        if (archive.SessionElement is null && archive.Checkpoints.Count == 0)
        {
            File.Delete(GetSessionPath(userId, thread.Id));
            return;
        }
        await WriteSessionArchiveAsync(userId, thread.Id, archive, ct).ConfigureAwait(false);
    }

    private static ChatThread MapSummary(ChatSessionIndexEntry row, string userId)
    {
        return new ChatThread
        {
            Id = row.SessionId,
            Title = row.Title,
            BranchOrigin = row.BranchOrigin,
            AgentId = row.AgentId,
            ProjectId = row.ProjectId,
            AgentName = row.AgentName,
            UpdatedAt = row.UpdatedAt,
            IsUnread = row.IsUnread,
            IsCurrent = row.IsCurrent,
            LastIndexedMessageCount = row.LastIndexedMessageCount,
            LastSummarizedMessageCount = row.LastSummarizedMessageCount,
            LastReflectedMessageCount = row.LastReflectedMessageCount,
            LastSummarizedMessageOffset = row.LastSummarizedMessageOffset,
            LastReflectedMessageOffset = row.LastReflectedMessageOffset
        };
    }


    private static void UpsertIndexRow(
            List<ChatSessionIndexEntry> rows,
            ChatThread thread,
            ChatSessionIndexEntry? existing,
            bool setCurrent,
            string userId)
    {
        var row = rows.FirstOrDefault(entry => string.Equals(entry.SessionId, thread.Id, StringComparison.OrdinalIgnoreCase));
        var now = DateTimeOffset.UtcNow;
        if (row is null)
        {
            row = new ChatSessionIndexEntry
            {
                SessionId = thread.Id,
                CreatedAt = existing?.CreatedAt ?? now
            };
            rows.Add(row);
        }

        row.Channel = ResolveThreadChannel(thread, row);
        row.Title = thread.Title;
        row.BranchOrigin = thread.BranchOrigin;
        row.AgentId = thread.AgentId ?? row.AgentId;
        row.ProjectId = thread.ProjectId;
        row.AgentName = thread.AgentName ?? row.AgentName;
        row.UpdatedAt = now;
        row.IsUnread = thread.IsUnread;
        row.LastIndexedMessageCount = existing is not null && row.LastIndexedMessageCount == 0
            ? 0
            : Math.Max(row.LastIndexedMessageCount, thread.LastIndexedMessageCount);
        // A live transcript save must not erase fragments already committed by maintenance.
        (row.LastSummarizedMessageCount, row.LastSummarizedMessageOffset) = FurthestProgress(
            row.LastSummarizedMessageCount, row.LastSummarizedMessageOffset,
            thread.LastSummarizedMessageCount, thread.LastSummarizedMessageOffset);
        (row.LastReflectedMessageCount, row.LastReflectedMessageOffset) = FurthestProgress(
            row.LastReflectedMessageCount, row.LastReflectedMessageOffset,
            thread.LastReflectedMessageCount, thread.LastReflectedMessageOffset);

        if (setCurrent && !string.IsNullOrEmpty(row.AgentId))
        {
            foreach (var other in rows.Where(entry =>
                         string.Equals(entry.AgentId, row.AgentId, StringComparison.Ordinal) &&
                         string.Equals(entry.ProjectId, row.ProjectId, StringComparison.Ordinal) &&
                         !string.Equals(entry.SessionId, thread.Id, StringComparison.OrdinalIgnoreCase)))
            {
                other.IsCurrent = false;
            }

            row.IsCurrent = true;
        }
        else if (!setCurrent && row.CreatedAt == row.UpdatedAt)
        {
            row.IsCurrent = false;
        }
    }

    // Progress is ordered by message count first, then by consumed text inside the next message.
    private static (int Count, int Offset) FurthestProgress(int count, int offset, int candidateCount, int candidateOffset)
        => candidateCount > count ? (candidateCount, candidateOffset) :
            candidateCount == count ? (count, Math.Max(offset, candidateOffset)) : (count, offset);

    private List<ChatSessionIndexEntry> ReadIndex(string userId)
    {
        if (!contentProtector.IsAvailable(userId)) { return []; }
        var indexPath = GetIndexPath(userId);
        if (!File.Exists(indexPath))
        {
            return [];
        }

        var rows = new List<ChatSessionIndexEntry>();
        var lines = FileUtil.ReadAllLinesShared(indexPath);
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var row = JsonSerializer.Deserialize<ChatSessionIndexEntry>(
                    contentProtector.Unprotect(userId, IndexPurpose, line)
                        ?? throw new InvalidOperationException("The encrypted conversation index cannot be read."), JsonOptions);
                if (row is not null && !string.IsNullOrWhiteSpace(row.SessionId))
                {
                    rows.Add(row);
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Skipping malformed chat index line {LineNumber} in '{IndexPath}'.", index + 1, indexPath);
            }
        }

        return rows;
    }

    private async Task WriteIndexAsync(string userId, IReadOnlyList<ChatSessionIndexEntry> rows, CancellationToken ct)
    {
        var content = rows.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, rows.Select(row => contentProtector.Protect(userId, IndexPurpose, JsonSerializer.Serialize(row, JsonOptions)))) + Environment.NewLine;

        await FileUtil.WriteAllTextAtomicAsync(GetIndexPath(userId), content, ct).ConfigureAwait(false);
    }

    private static async Task WriteTranscriptLinesAsync(string transcriptPath, IReadOnlyList<string> lines, CancellationToken ct)
    {
        var content = lines.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, lines) + Environment.NewLine;

        await FileUtil.WriteAllTextAtomicAsync(transcriptPath, content, ct).ConfigureAwait(false);
    }

    private int? FindHeaderLine(string userId, string threadId, IReadOnlyList<string> lines, string transcriptPath)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index]))
            {
                continue;
            }

            try
            {
                var record = DeserializeTranscriptRecord(contentProtector.Unprotect(userId, TranscriptPurpose(threadId), lines[index])
                        ?? throw new InvalidOperationException("The encrypted transcript cannot be read."));
                if (string.Equals(record?.Kind, HeaderKind, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Skipping malformed chat transcript line {LineNumber} in '{TranscriptPath}'.", index + 1, transcriptPath);
            }
        }

        return null;
    }

    private ChatSessionIndexEntry? TryReadIndexEntry(string userId, string threadId)
    {
        return ReadIndex(userId)
            .FirstOrDefault(row => string.Equals(row.SessionId, threadId, StringComparison.OrdinalIgnoreCase));
    }

    // Walk backward to a complete exchange so a display page never starts in the middle of a reply.
    private void ReadTranscriptPage(string userId, string threadId, ChatThread thread, int limit,
        string? beforeMessageId, string? firstMessageId)
    {
        var path = GetTranscriptPath(userId, threadId);
        var lines = File.Exists(path) ? FileUtil.ReadAllLinesShared(path) : [];
        if (lines.Length > 0) { thread.SystemInstruction = ReadTranscriptRecord(userId, threadId, lines[0], 0, path)?.SystemInstruction; }
        var messages = new List<ChatDisplayMessage>();
        var foundBefore = beforeMessageId is null;
        var foundFirst = firstMessageId is null;
        var index = lines.Length - 1;
        for (; index > 0; index--)
        {
            var message = ReadTranscriptRecord(userId, threadId, lines[index], index, path)?.Message;
            if (message is null) { continue; }
            if (!foundBefore)
            {
                if (message.MessageId == beforeMessageId) { foundBefore = true; }
                continue;
            }
            messages.Add(message);
            if (firstMessageId is not null)
            {
                if (message.MessageId == firstMessageId) { foundFirst = true; break; }
            }
            else if (messages.Count >= limit && message.From == MessageOwner.User && !message.IsInteractionResponse)
            {
                break;
            }
        }
        if (!foundBefore || !foundFirst) { throw new ArgumentException("The history cursor no longer exists. Reopen the conversation."); }
        for (index--; index > 0; index--)
        {
            if (ReadTranscriptRecord(userId, threadId, lines[index], index, path)?.Message is not null)
            {
                thread.HasOlderDisplayMessages = true;
                break;
            }
        }
        messages.Reverse();
        thread.DisplayMessages = messages;
    }

    private ChatTranscriptRecord? ReadTranscriptRecord(string userId, string threadId, string line, int index, string path)
    {
        if (string.IsNullOrWhiteSpace(line)) { return null; }
        try
        {
            return DeserializeTranscriptRecord(contentProtector.Unprotect(userId, TranscriptPurpose(threadId), line)
                ?? throw new InvalidOperationException("The encrypted transcript cannot be read."));
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Skipping malformed chat transcript line {LineNumber} in '{TranscriptPath}'.", index + 1, path);
            return null;
        }
    }

    private (List<ChatDisplayMessage> Messages, string? SystemInstruction) ReadTranscript(string userId, string threadId)
    {
        var transcriptPath = GetTranscriptPath(userId, threadId);
        if (!File.Exists(transcriptPath))
        {
            return ([], null);
        }

        var messages = new List<ChatDisplayMessage>();
        string? systemInstruction = null;
        var lines = FileUtil.ReadAllLinesShared(transcriptPath);
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var record = ReadTranscriptRecord(userId, threadId, line, index, transcriptPath);
            if (record is null) { continue; }
            if (string.Equals(record.Kind, HeaderKind, StringComparison.OrdinalIgnoreCase))
            {
                systemInstruction = record.SystemInstruction;
            }
            else if (string.Equals(record.Kind, MessageKind, StringComparison.OrdinalIgnoreCase) && record.Message is not null)
            {
                messages.Add(record.Message);
            }
        }

        return (messages, systemInstruction);
    }

    private List<string> ReadTranscriptMessageIds(string userId, string threadId, string transcriptPath)
    {
        var ids = new List<string>();
        if (!File.Exists(transcriptPath))
        {
            return ids;
        }

        var lines = FileUtil.ReadAllLinesShared(transcriptPath);
        for (var index = 0; index < lines.Length; index++)
        {
            try
            {
                var record = DeserializeTranscriptRecord(contentProtector.Unprotect(userId, TranscriptPurpose(threadId), lines[index])
                        ?? throw new InvalidOperationException("The encrypted transcript cannot be read."));
                if (record is not null &&
                    string.Equals(record.Kind, MessageKind, StringComparison.OrdinalIgnoreCase) &&
                    record.Message is not null)
                {
                    ids.Add(record.Message.MessageId);
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Skipping malformed chat transcript line {LineNumber} in '{TranscriptPath}'.", index + 1, transcriptPath);
            }
        }

        return ids;
    }

    /// <summary>Keeps current and historical native state inside one encrypted session artifact.</summary>
    private sealed class SessionArchive
    {
        public const int CurrentVersion = 1;
        [JsonPropertyName("v")]
        public int Version { get; set; } = CurrentVersion;
        public JsonElement? SessionElement { get; set; }
        public Dictionary<string, JsonElement> Checkpoints { get; set; } = new(StringComparer.Ordinal);
    }

    // The transcript determines which retained checkpoints remain valid branch targets.
    private static void SetCheckpoints(ChatThread thread, IReadOnlyDictionary<string, JsonElement> checkpoints)
    {
        var ids = thread.DisplayMessages.Select(message => message.MessageId).ToHashSet(StringComparer.Ordinal);
        var retained = checkpoints.Where(pair => ids.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
        thread.SessionCheckpoints = retained;
        thread.BranchableMessageIds = thread.DisplayMessages
            .Where(message => retained.ContainsKey(message.MessageId))
            .Select(message => message.MessageId).ToArray();
    }

    private SessionArchive? ReadSessionArchive(string userId, string threadId)
    {
        var sessionPath = GetSessionPath(userId, threadId);
        if (!File.Exists(sessionPath)) { return null; }
        // Atomic publication briefly holds delete access on the replacement file.
        using var stream = new FileStream(sessionPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var opened = contentProtector.Unprotect(userId, SessionPurpose(threadId), reader.ReadToEnd())
            ?? throw new InvalidOperationException("The encrypted session cannot be read.");
        try
        {
            using var document = JsonDocument.Parse(opened);
            RequireRecordVersion(document.RootElement, SessionArchive.CurrentVersion, "native session archive");
            return JsonSerializer.Deserialize<SessionArchive>(document.RootElement, JsonOptions)
                ?? throw new JsonException("The session archive is empty.");
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to read chat session file '{SessionPath}'.", sessionPath);
            throw new InvalidOperationException("The saved chat context cannot be read.", ex);
        }
    }

    private Task WriteSessionArchiveAsync(string userId, string threadId, SessionArchive archive, CancellationToken ct)
    {
        var content = contentProtector.Protect(userId, SessionPurpose(threadId),
            JsonSerializer.Serialize(archive, JsonOptions));
        return FileUtil.WriteAllTextAtomicAsync(GetSessionPath(userId, threadId), content, ct);
    }

    private JsonElement? ReadSessionElement(string userId, string threadId) =>
        ReadSessionArchive(userId, threadId)?.SessionElement;

    /// <summary>Checks transcript interpretation before binding display fields.</summary>
    private static ChatTranscriptRecord? DeserializeTranscriptRecord(string json)
    {
        using var document = JsonDocument.Parse(json);
        RequireRecordVersion(document.RootElement, ChatTranscriptRecord.CurrentVersion, "chat transcript");
        return JsonSerializer.Deserialize<ChatTranscriptRecord>(document.RootElement, JsonOptions);
    }

    /// <summary>Refuses unsupported versions while allowing the original unmarked object format.</summary>
    private static void RequireRecordVersion(JsonElement document, int supportedVersion, string description)
    {
        if (document.ValueKind != JsonValueKind.Object) { throw new JsonException($"The {description} must be an object."); }
        if (document.TryGetProperty("v", out var marker) &&
            (marker.ValueKind != JsonValueKind.Number || !marker.TryGetInt32(out var version) || version != supportedVersion))
        {
            throw new InvalidDataException($"The saved {description} version is unsupported. Use a compatible application or restore a compatible backup.");
        }
    }

    private async Task<ChatDisplayMessage> RedactDisplayMessageAsync(string userId, ChatDisplayMessage message, CancellationToken ct)
    {
        return new ChatDisplayMessage
        {
            Channel = string.IsNullOrWhiteSpace(message.Channel) ? ChatChannels.Web : message.Channel,
            MessageId = message.MessageId,
            CreatedAt = message.CreatedAt,
            From = message.From,
            AgentId = message.AgentId,
            AgentName = message.AgentName,
            RecipientAgentId = message.RecipientAgentId,
            IsInteractionResponse = message.IsInteractionResponse,
            HistorySummarized = message.HistorySummarized,
            Text = await redactionService.RedactTextAsync(message.Text, userId, ct).ConfigureAwait(false),
            Segments = await RedactSegmentsAsync(userId, message.Segments, ct).ConfigureAwait(false),
            Elicitation = message.Elicitation,
            ToolApproval = message.ToolApproval,
            Attachments = message.Attachments,
            WorkspaceFiles = message.WorkspaceFiles,
            InputTokens = message.InputTokens,
            OutputTokens = message.OutputTokens,
            TotalTokens = message.TotalTokens
        };
    }

    private async Task<IList<ChatSegment>> RedactSegmentsAsync(string userId, IEnumerable<ChatSegment> segments, CancellationToken ct)
    {
        var redacted = new List<ChatSegment>();
        foreach (var segment in segments)
        {
            var functionCall = await RedactFunctionCallAsync(userId, segment.FunctionCall, ct).ConfigureAwait(false);
            var functionResult = await RedactFunctionResultAsync(userId, segment.FunctionResult, ct).ConfigureAwait(false);
            redacted.Add(new ChatSegment(
                segment.Type,
                segment.State,
                segment.CreatedAt,
                segment.ProcessedAt,
                await redactionService.RedactTextAsync(segment.Text, userId, ct),
                functionCall,
                functionResult)
            {
                ExecutionState = segment.ExecutionState
            });
        }

        return redacted;
    }

    private async Task<FunctionCallContent?> RedactFunctionCallAsync(
        string userId,
        FunctionCallContent? functionCall,
        CancellationToken ct)
    {
        if (functionCall is null)
        {
            return null;
        }

        var arguments = functionCall.Arguments is null
            ? null
            : await redactionService.RedactArgumentsAsync(functionCall.Arguments, userId, ct).ConfigureAwait(false);

        return new FunctionCallContent(functionCall.CallId, functionCall.Name, arguments)
        {
            Annotations = functionCall.Annotations,
            RawRepresentation = functionCall.RawRepresentation,
            AdditionalProperties = functionCall.AdditionalProperties,
            Exception = functionCall.Exception,
            InformationalOnly = functionCall.InformationalOnly
        };
    }

    private async Task<FunctionResultContent?> RedactFunctionResultAsync(
        string userId,
        FunctionResultContent? functionResult,
        CancellationToken ct)
    {
        if (functionResult is null)
        {
            return null;
        }

        var redactedResult = functionResult.Result is null
            ? null
            : await redactionService.RedactTextAsync(functionResult.Result.ToString() ?? string.Empty, userId, ct).ConfigureAwait(false);

        return new FunctionResultContent(functionResult.CallId, redactedResult)
        {
            Exception = functionResult.Exception
        };
    }

    private static string ResolveThreadChannel(ChatThread thread, ChatSessionIndexEntry? existing)
    {
        return thread.DisplayMessages.FirstOrDefault(message => !string.IsNullOrWhiteSpace(message.Channel))?.Channel
            ?? existing?.Channel
            ?? ChatChannels.Web;
    }

    private string GetChatDirectory(string userId)
    {
        if (!PathHelper.IsSafePathSegment(userId))
        {
            throw new ArgumentException($"Invalid user id '{userId}'.", nameof(userId));
        }

        if (!PathHelper.TryResolveContainedPath(
                _usersRootResolver(),
                Path.Combine(userId, ChatDirectory),
                out var chatDirectory))
        {
            throw new UnauthorizedAccessException($"Chat path escapes the configured users root: {userId}");
        }

        return chatDirectory;
    }

    private string GetIndexPath(string userId)
    {
        var chatDirectory = GetChatDirectory(userId);
        if (!PathHelper.TryResolveContainedPath(chatDirectory, SessionIndexFile, out var indexPath))
        {
            throw new UnauthorizedAccessException($"Chat index path contains a symbolic link or reparse point: {userId}");
        }

        return indexPath;
    }

    private string GetTranscriptPath(string userId, string threadId)
    {
        return ResolveContainedSidecarPath(userId, threadId, SessionTranscriptExtension);
    }

    private string GetSessionPath(string userId, string threadId)
    {
        return ResolveContainedSidecarPath(userId, threadId, SessionFileSuffix);
    }

    private void ValidateThreadId(string userId, string threadId)
    {
        if (!PathHelper.IsSafePathSegment(threadId))
        {
            throw new ArgumentException($"Invalid thread id '{threadId}'.", nameof(threadId));
        }

        _ = GetTranscriptPath(userId, threadId);
        _ = GetSessionPath(userId, threadId);
    }

    private string ResolveContainedSidecarPath(string userId, string threadId, string suffix)
    {
        var chatDirectory = GetChatDirectory(userId);
        if (!PathHelper.TryResolveContainedPath(chatDirectory, threadId + suffix, out var candidate))
        {
            throw new ArgumentException($"Invalid thread id '{threadId}'.", nameof(threadId));
        }

        return candidate;
    }

    /// <summary>Keeps user identity exact while matching this class's case-insensitive thread identity.</summary>
    private sealed class IncognitoKeyComparer : IEqualityComparer<(string UserId, string ThreadId)>
    {
        public static IncognitoKeyComparer Instance { get; } = new();

        public bool Equals((string UserId, string ThreadId) left, (string UserId, string ThreadId) right)
        {
            return string.Equals(left.UserId, right.UserId, StringComparison.Ordinal) &&
                   string.Equals(left.ThreadId, right.ThreadId, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode((string UserId, string ThreadId) key)
        {
            return HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(key.UserId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(key.ThreadId));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Deleting sidecar files is best-effort after the index entry is removed.
        }
    }
}
