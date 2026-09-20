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
using Oscova.Agents.Definitions;
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
    IAgentDefinitionRepository agentDefinitions,
    ILogger<ChatThreadRepository> logger) : IChatThreadRepository
{
    private const string ChatDirectory = "chat";
    private const string SessionIndexFile = "index.jsonl";
    private const string SessionTranscriptExtension = ".jsonl";
    private const string SessionFileSuffix = ".session.json";
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
    private static string TitlePurpose(string threadId) => $"chat\0{threadId}\0title";
    private static string InstructionPurpose(string threadId) => $"chat\0{threadId}\0instruction";
    private static string TranscriptPurpose(string threadId) => $"chat\0{threadId}\0transcript";
    private static string SessionPurpose(string threadId) => $"chat\0{threadId}\0session";

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChatThread>> GetThreadsAsync(
        string userId,
        bool includeEncrypted = true,
        CancellationToken ct = default)
    {
        var rows = ReadIndex(userId);
        var visible = new List<ChatSessionIndexEntry>(rows.Count);
        var agentStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var canOpenEncrypted = contentProtector.IsAvailable(userId);
        foreach (var row in rows)
        {
            if (!includeEncrypted || !canOpenEncrypted)
            {
                if (row.IsEncrypted)
                {
                    continue;
                }

                var agentKey = row.AgentId ?? string.Empty;
                if (!agentStates.TryGetValue(agentKey, out var isConfidential))
                {
                    isConfidential = await IsConfidentialAsync(userId, row.AgentId, ct).ConfigureAwait(false);
                    agentStates[agentKey] = isConfidential;
                }

                if (isConfidential)
                {
                    continue;
                }
            }

            visible.Add(row);
        }

        return visible
            .OrderByDescending(row => row.UpdatedAt)
            .Select(row => MapSummary(row, userId))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<ChatThread?> GetThreadAsync(string userId, string threadId, CancellationToken ct = default)
    {
        ValidateThreadId(userId, threadId);
        if (_incognitoThreads.TryGetValue((userId, threadId), out var incognitoThread))
        {
            // Callers mutate the thread and save it back, so hand out the live instance rather than a copy.
            return incognitoThread;
        }

        var row = TryReadIndexEntry(userId, threadId);
        if (row is null)
        {
            return null;
        }

        if (!contentProtector.IsAvailable(userId) &&
            (row.IsEncrypted || await IsConfidentialAsync(userId, row.AgentId, ct).ConfigureAwait(false)))
        {
            return null;
        }

        var (messages, systemInstruction) = ReadTranscript(userId, row.SessionId);
        var thread = MapSummary(row, userId);
        thread.DisplayMessages = messages;
        thread.SystemInstruction = systemInstruction;
        thread.SessionElement = ReadSessionElement(userId, row.SessionId);

        return thread;
    }

    /// <inheritdoc />
    public async Task SaveThreadAsync(
        string userId,
        ChatThread thread,
        bool setCurrent = true,
        CancellationToken ct = default)
    {
        ValidateThreadId(userId, thread.Id);
        if (thread.IsIncognito)
        {
            thread.IsCurrent = false;
            thread.UpdatedAt = DateTimeOffset.UtcNow;
            _incognitoThreads[(userId, thread.Id)] = thread;
            return;
        }

        // A thread saved without the flag must not stay behind in memory under the same id.
        _incognitoThreads.TryRemove((userId, thread.Id), out _);

        await indexCoordinator.RunForUserAsync(userId, async cancellationToken =>
        {
            var existing = TryReadIndexEntry(userId, thread.Id);
            if (existing is null && thread.IsEncrypted)
            {
                // Authoritative deletion must win over a confidential run that finishes later.
                return;
            }

            thread.Id = existing?.SessionId ?? thread.Id;
            thread.IsEncrypted = await IsConfidentialAsync(userId, thread.AgentId, cancellationToken).ConfigureAwait(false);
            await WriteTranscriptForStateAsync(userId, thread, existing, cancellationToken).ConfigureAwait(false);
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
            if (row is null || row.IsEncrypted && !contentProtector.IsAvailable(userId))
            {
                // Metadata updates may only mutate an existing readable record so deletion remains final.
                return;
            }

            row.Title = row.IsEncrypted && !string.IsNullOrWhiteSpace(title)
                ? contentProtector.Protect(userId, TitlePurpose(row.SessionId), title)
                : title;
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
            var isEncrypted = row.IsEncrypted;
            var sessionPath = GetSessionPath(userId, storedThreadId);
            var raw = sessionElement.GetRawText();
            var content = isEncrypted
                ? JsonSerializer.Serialize(
                    new ProtectedSessionFile
                    {
                        IsEncrypted = true,
                        ProtectedPayload = contentProtector.Protect(userId, SessionPurpose(storedThreadId), raw)
                    },
                    JsonOptions)
                : raw;
            await FileUtil.WriteAllTextAtomicAsync(sessionPath, content, cancellationToken).ConfigureAwait(false);

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
        int? lastReflectedMessageCount = null)
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
                row.LastSummarizedMessageCount = lastSummarizedMessageCount.Value;
            }

            if (lastReflectedMessageCount.HasValue)
            {
                row.LastReflectedMessageCount = lastReflectedMessageCount.Value;
            }

            row.UpdatedAt = DateTimeOffset.UtcNow;
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
            foreach (var row in rows.Where(row => string.Equals(row.AgentId, agentId, StringComparison.Ordinal)))
            {
                row.IsCurrent = string.Equals(row.SessionId, threadId, StringComparison.OrdinalIgnoreCase);
            }

            await WriteIndexAsync(userId, rows, cancellationToken).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ClearCurrentAsync(string userId, string agentId, CancellationToken ct = default)
    {
        await indexCoordinator.RunForUserAsync(userId, async cancellationToken =>
        {
            var rows = ReadIndex(userId);
            var changed = false;
            foreach (var row in rows.Where(row => string.Equals(row.AgentId, agentId, StringComparison.Ordinal) && row.IsCurrent))
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
        var incognitoCount = RemoveIncognitoThreads(userId, static _ => true);

        List<ChatSessionIndexEntry> rows = [];
        await indexCoordinator.RunForUserAsync(userId, async cancellationToken =>
        {
            rows = ReadIndex(userId);
            foreach (var row in rows)
            {
                ValidateThreadId(userId, row.SessionId);
            }

            await WriteIndexAsync(userId, [], cancellationToken).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        foreach (var row in rows)
        {
            TryDelete(GetTranscriptPath(userId, row.SessionId));
            TryDelete(GetSessionPath(userId, row.SessionId));
        }

        return rows.Count + incognitoCount;
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

    private async Task<bool> IsConfidentialAsync(string userId, string? agentId, CancellationToken ct)
    {
        var agent = string.IsNullOrWhiteSpace(agentId)
            ? await agentDefinitions.GetMainAgentAsync(userId, ct).ConfigureAwait(false)
            : await agentDefinitions.GetAgentAsync(userId, agentId, ct).ConfigureAwait(false);

        return agent?.IsConfidential == true;
    }

    private string SerializeRecord(ChatTranscriptRecord record, bool encrypt, string userId, string threadId)
    {
        if (!encrypt)
        {
            return JsonSerializer.Serialize(record, JsonOptions);
        }

        if (string.Equals(record.Kind, HeaderKind, StringComparison.OrdinalIgnoreCase))
        {
            record.IsEncrypted = true;
            if (!string.IsNullOrWhiteSpace(record.SystemInstruction))
            {
                record.ProtectedPayload = contentProtector.Protect(
                    userId,
                    InstructionPurpose(threadId),
                    record.SystemInstruction);
                record.SystemInstruction = null;
            }
        }
        else if (record.Message is not null)
        {
            record.IsEncrypted = true;
            record.ProtectedPayload = contentProtector.Protect(
                userId,
                TranscriptPurpose(threadId),
                JsonSerializer.Serialize(record.Message, JsonOptions));
            record.Message = null;
        }

        return JsonSerializer.Serialize(record, JsonOptions);
    }

    private bool? TryReadTranscriptState(string userId, string threadId)
    {
        var transcriptPath = GetTranscriptPath(userId, threadId);
        if (!File.Exists(transcriptPath))
        {
            return null;
        }

        var lines = FileUtil.ReadAllLinesShared(transcriptPath).ToList();
        var headerIndex = FindHeaderLine(lines, transcriptPath)
            ?? throw new InvalidOperationException($"Thread '{threadId}' has a transcript with no header record.");

        try
        {
            return JsonSerializer.Deserialize<ChatTranscriptRecord>(lines[headerIndex], JsonOptions)?.IsEncrypted
                ?? throw new InvalidOperationException($"Thread '{threadId}' has an unreadable transcript header.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Thread '{threadId}' has an unreadable transcript header.", ex);
        }
    }

    private void OpenRecord(ChatTranscriptRecord record, string userId, string threadId)
    {
        if (!record.IsEncrypted)
        {
            return;
        }

        var isHeader = string.Equals(record.Kind, HeaderKind, StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(record.ProtectedPayload))
        {
            if (isHeader)
            {
                record.IsEncrypted = false;
                return;
            }

            throw new InvalidOperationException($"Thread '{threadId}' has an encrypted record with no payload.");
        }

        var purpose = isHeader ? InstructionPurpose(threadId) : TranscriptPurpose(threadId);
        var opened = contentProtector.Unprotect(userId, purpose, record.ProtectedPayload)
            ?? throw new InvalidOperationException($"Thread '{threadId}' is stored encrypted and cannot be read.");

        if (isHeader)
        {
            record.SystemInstruction = opened;
        }
        else
        {
            try
            {
                record.Message = JsonSerializer.Deserialize<ChatDisplayMessage>(opened, JsonOptions)
                    ?? throw new InvalidOperationException($"Thread '{threadId}' has an unreadable encrypted message.");
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Thread '{threadId}' has an unreadable encrypted message.", ex);
            }
        }

        record.IsEncrypted = false;
        record.ProtectedPayload = null;
    }

    private async Task WriteTranscriptForStateAsync(
        string userId,
        ChatThread thread,
        ChatSessionIndexEntry? existing,
        CancellationToken ct)
    {
        if (thread.IsEncrypted && existing is { IsEncrypted: false })
        {
            var rows = ReadIndex(userId);
            var row = rows.FirstOrDefault(entry =>
                string.Equals(entry.SessionId, thread.Id, StringComparison.OrdinalIgnoreCase));
            if (row is not null)
            {
                row.IsEncrypted = true;
                row.Title = string.Empty;
                await WriteIndexAsync(userId, rows, ct).ConfigureAwait(false);
            }
        }

        var storedState = TryReadTranscriptState(userId, thread.Id);
        if (storedState is not null && storedState.Value != thread.IsEncrypted)
        {
            await RewriteTranscriptAsync(userId, thread, existing, ct).ConfigureAwait(false);
            return;
        }

        await AppendNewTranscriptMessagesAsync(userId, thread, existing, ct).ConfigureAwait(false);
    }

    private async Task AppendNewTranscriptMessagesAsync(
        string userId,
        ChatThread thread,
        ChatSessionIndexEntry? existing,
        CancellationToken ct)
    {
        var transcriptPath = GetTranscriptPath(userId, thread.Id);
        var existingCount = CountTranscriptMessages(transcriptPath);
        if (existingCount > thread.DisplayMessages.Count)
        {
            logger.LogWarning(
                "Thread {ThreadId} for user {UserId} has fewer supplied messages than persisted transcript rows. Rewriting transcript.",
                thread.Id,
                userId);
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
                SerializeRecord(record, thread.IsEncrypted, userId, thread.Id),
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
                thread.IsEncrypted,
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
            }, thread.IsEncrypted, userId, thread.Id));
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
            SerializeRecord(header, thread.IsEncrypted, userId, thread.Id),
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
        var headerIndex = FindHeaderLine(lines, transcriptPath);

        if (headerIndex is not null)
        {
            var stored = JsonSerializer.Deserialize<ChatTranscriptRecord>(lines[headerIndex.Value], JsonOptions);
            if (stored is not null)
            {
                OpenRecord(stored, userId, thread.Id);
                if (string.Equals(
                        JsonSerializer.Serialize(stored, JsonOptions),
                        JsonSerializer.Serialize(header, JsonOptions),
                        StringComparison.Ordinal))
                {
                    return;
                }
            }
        }

        var serializedHeader = SerializeRecord(header, thread.IsEncrypted, userId, thread.Id);

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
            CreatedAt = existing?.CreatedAt ?? DateTimeOffset.UtcNow,
            SystemInstruction = string.IsNullOrWhiteSpace(thread.SystemInstruction)
                ? null
                : await redactionService.RedactTextAsync(thread.SystemInstruction, userId, ct).ConfigureAwait(false)
        };
    }

    private async Task WriteSessionAsync(string userId, ChatThread thread, CancellationToken ct)
    {
        var sessionPath = GetSessionPath(userId, thread.Id);
        if (thread.SessionElement is null)
        {
            File.Delete(sessionPath);
            return;
        }

        var raw = thread.SessionElement.Value.GetRawText();
        var content = thread.IsEncrypted
            ? JsonSerializer.Serialize(
                new ProtectedSessionFile
                {
                    IsEncrypted = true,
                    ProtectedPayload = contentProtector.Protect(userId, SessionPurpose(thread.Id), raw)
                },
                JsonOptions)
            : raw;

        await FileUtil.WriteAllTextAtomicAsync(sessionPath, content, ct).ConfigureAwait(false);
    }

    private ChatThread MapSummary(ChatSessionIndexEntry row, string userId)
    {
        return new ChatThread
        {
            Id = row.SessionId,
            Title = row.IsEncrypted && !string.IsNullOrWhiteSpace(row.Title)
                ? contentProtector.Unprotect(userId, TitlePurpose(row.SessionId), row.Title)
                    ?? throw new InvalidOperationException($"Thread '{row.SessionId}' is stored encrypted and cannot be read.")
                : row.Title,
            AgentId = row.AgentId,
            AgentName = row.AgentName,
            UpdatedAt = row.UpdatedAt,
            IsUnread = row.IsUnread,
            IsCurrent = row.IsCurrent,
            IsEncrypted = row.IsEncrypted,
            LastIndexedMessageCount = row.LastIndexedMessageCount,
            LastSummarizedMessageCount = row.LastSummarizedMessageCount,
            LastReflectedMessageCount = row.LastReflectedMessageCount
        };
    }

    private void UpsertIndexRow(
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
        row.Title = thread.IsEncrypted && !string.IsNullOrWhiteSpace(thread.Title)
            ? contentProtector.Protect(userId, TitlePurpose(thread.Id), thread.Title)
            : thread.Title;
        row.IsEncrypted = thread.IsEncrypted;
        row.AgentId = thread.AgentId ?? row.AgentId;
        row.AgentName = thread.AgentName ?? row.AgentName;
        row.UpdatedAt = now;
        row.IsUnread = thread.IsUnread;
        row.LastIndexedMessageCount = existing is not null && row.LastIndexedMessageCount == 0
            ? 0
            : Math.Max(row.LastIndexedMessageCount, thread.LastIndexedMessageCount);
        row.LastSummarizedMessageCount = Math.Max(row.LastSummarizedMessageCount, thread.LastSummarizedMessageCount);
        row.LastReflectedMessageCount = Math.Max(row.LastReflectedMessageCount, thread.LastReflectedMessageCount);

        if (setCurrent && !string.IsNullOrEmpty(row.AgentId))
        {
            foreach (var other in rows.Where(entry =>
                         string.Equals(entry.AgentId, row.AgentId, StringComparison.Ordinal) &&
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

    private List<ChatSessionIndexEntry> ReadIndex(string userId)
    {
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
                var row = JsonSerializer.Deserialize<ChatSessionIndexEntry>(line, JsonOptions);
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
            : string.Join(Environment.NewLine, rows.Select(row => JsonSerializer.Serialize(row, JsonOptions))) + Environment.NewLine;

        await FileUtil.WriteAllTextAtomicAsync(GetIndexPath(userId), content, ct).ConfigureAwait(false);
    }

    private static async Task WriteTranscriptLinesAsync(string transcriptPath, IReadOnlyList<string> lines, CancellationToken ct)
    {
        var content = lines.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, lines) + Environment.NewLine;

        await FileUtil.WriteAllTextAtomicAsync(transcriptPath, content, ct).ConfigureAwait(false);
    }

    private int? FindHeaderLine(IReadOnlyList<string> lines, string transcriptPath)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index]))
            {
                continue;
            }

            try
            {
                var record = JsonSerializer.Deserialize<ChatTranscriptRecord>(lines[index], JsonOptions);
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

            try
            {
                var record = JsonSerializer.Deserialize<ChatTranscriptRecord>(line, JsonOptions);
                if (record is null)
                {
                    continue;
                }

                OpenRecord(record, userId, threadId);

                if (string.Equals(record.Kind, HeaderKind, StringComparison.OrdinalIgnoreCase))
                {
                    systemInstruction = record.SystemInstruction;
                }
                else if (string.Equals(record.Kind, MessageKind, StringComparison.OrdinalIgnoreCase) && record.Message is not null)
                {
                    messages.Add(record.Message);
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Skipping malformed chat transcript line {LineNumber} in '{TranscriptPath}'.", index + 1, transcriptPath);
            }
        }

        return (messages, systemInstruction);
    }

    private int CountTranscriptMessages(string transcriptPath)
    {
        if (!File.Exists(transcriptPath))
        {
            return 0;
        }

        var count = 0;
        var lines = FileUtil.ReadAllLinesShared(transcriptPath);
        for (var index = 0; index < lines.Length; index++)
        {
            try
            {
                var record = JsonSerializer.Deserialize<ChatTranscriptRecord>(lines[index], JsonOptions);
                if (record is not null &&
                    string.Equals(record.Kind, MessageKind, StringComparison.OrdinalIgnoreCase) &&
                    (record.Message is not null || record.IsEncrypted))
                {
                    count++;
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Skipping malformed chat transcript line {LineNumber} in '{TranscriptPath}'.", index + 1, transcriptPath);
            }
        }

        return count;
    }

    private JsonElement? ReadSessionElement(string userId, string threadId)
    {
        var sessionPath = GetSessionPath(userId, threadId);
        if (!File.Exists(sessionPath))
        {
            return null;
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(sessionPath));
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to read chat session file '{SessionPath}'.", sessionPath);
            return null;
        }

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(nameof(ProtectedSessionFile.IsEncrypted), out var flag) &&
            flag.ValueKind == JsonValueKind.True)
        {
            if (!root.TryGetProperty(nameof(ProtectedSessionFile.ProtectedPayload), out var payload) ||
                string.IsNullOrEmpty(payload.GetString()))
            {
                throw new InvalidOperationException($"Session for thread '{threadId}' is marked encrypted but carries no payload.");
            }

            var opened = contentProtector.Unprotect(userId, SessionPurpose(threadId), payload.GetString()!)
                ?? throw new InvalidOperationException($"Session for thread '{threadId}' is stored encrypted and cannot be read.");

            try
            {
                using var inner = JsonDocument.Parse(opened);
                return inner.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Session for thread '{threadId}' has an unreadable encrypted payload.", ex);
            }
        }

        return root;
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
            IsInteractionResponse = message.IsInteractionResponse,
            Text = await redactionService.RedactTextAsync(message.Text, userId, ct).ConfigureAwait(false),
            Segments = await RedactSegmentsAsync(userId, message.Segments, ct).ConfigureAwait(false),
            Elicitation = message.Elicitation,
            ToolApproval = message.ToolApproval,
            Attachments = message.Attachments,
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
