using Oscova.Agents.Conversations;
using System.Text.Json;
using Oscova.Agents.Core.ApiContracts.Chat;
using Microsoft.Extensions.Options;
using Oscova.Agents.Notifications;
using Oscova.Agents.Core.Mcp;

namespace Oscova.Agents.Host.Server.Chat;

/// <summary>Describes the display phase of one admitted chat run.</summary>
internal enum ChatRunProjectionPhase
{
    Starting,
    Streaming,
    Continuing,
    AwaitingElicitation,
    AwaitingToolApproval,
    AwaitingSubagents,
    Terminal
}

/// <summary>Describes the terminal result of one admitted chat run.</summary>
internal enum ChatRunOutcome
{
    None,
    Completed,
    Stopped,
    Failed
}

/// <summary>Classifies the projection change that woke subscribers.</summary>
internal enum ChatRunChangeKind
{
    Delta,
    Structural,
    Terminal
}

/// <summary>Wakes a user's panels so they can read the latest run snapshot.</summary>
internal sealed record ChatRunSignal(
    string ThreadId,
    string RunId,
    long InstanceId,
    long Sequence,
    ChatRunChangeKind Kind);

/// <summary>Contains one detached authoritative display slice for an admitted run.</summary>
internal sealed record ChatRunSnapshot(
    string ThreadId,
    string RunId,
    string UserMessageId,
    long InstanceId,
    long Sequence,
    ChatRunProjectionPhase Phase,
    ChatRunOutcome Outcome,
    IReadOnlyList<ChatDisplayMessage>? RunMessages,
    bool NodeUnavailable = false,
    McpConnectionException? McpFailure = null, ChatPermissionPreset? PermissionPreset = null,
    IReadOnlyList<ChatTodoItem>? Todos = null, bool ProjectFolderUnavailable = false);

/// <summary>Publishes state for one exact admitted chat run.</summary>
internal sealed class ChatRunProjection(
    ChatRunProjectionStore owner,
    string userId,
    ChatRunProjectionStore.Entry entry)
{
    internal string UserMessageId => entry.UserMessageId;

    /// <summary>Captures the admitted preset independently of the saved next-run choice.</summary>
    internal void SetPermissionPreset(ChatPermissionPreset preset) => owner.SetPermissionPreset(entry, preset);

    /// <summary>Notifies subscribers after this run has been admitted and tracked.</summary>
    internal void AnnounceStarting() => owner.AnnounceStarting(userId, entry);

    /// <summary>Publishes the latest display slice for this exact run.</summary>
    internal void Publish(
        IReadOnlyList<ChatDisplayMessage> runMessages,
        ChatRunProjectionPhase phase,
        ChatRunChangeKind kind,
        IReadOnlyList<ChatTodoItem>? todos = null) =>
        owner.Publish(userId, entry, runMessages, phase, kind, todos);

    /// <summary>Marks this exact run terminal while preserving its last display slice.</summary>
    internal void Complete(ChatRunOutcome outcome, bool nodeUnavailable = false, McpConnectionException? mcpFailure = null,
        bool projectFolderUnavailable = false) =>
        owner.Complete(userId, entry, outcome, nodeUnavailable, mcpFailure, projectFolderUnavailable);
}

/// <summary>Publishes detached display state for replaceable chat-panel subscribers.</summary>
public sealed class ChatRunProjectionStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Dictionary<string, Entry>> _entries =
        new(StringComparer.Ordinal);
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly NotificationHub<ChatRunSignal> _notifications;
    private long _nextInstanceId;

    internal ChatRunProjectionStore(
        IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions,
        NotificationHub<ChatRunSignal> notifications)
    {
        _serializerOptions = jsonOptions.Value.SerializerOptions;
        _notifications = notifications;
    }

    /// <summary>Creates or replaces the retained projection for one user thread.</summary>
    internal ChatRunProjection Begin(
        string userId,
        string threadId,
        string runId,
        string? userMessageId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(userId, out var userEntries))
            {
                userEntries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
                _entries[userId] = userEntries;
            }

            var entry = new Entry(
                threadId,
                runId,
                string.IsNullOrWhiteSpace(userMessageId)
                    ? $"msg_{Guid.NewGuid():N}"
                    : userMessageId,
                ++_nextInstanceId);
            userEntries[threadId] = entry;
            return new ChatRunProjection(this, userId, entry);
        }
    }

    internal void SetPermissionPreset(Entry entry, ChatPermissionPreset preset)
    {
        lock (_gate) { entry.PermissionPreset = preset; }
    }

    /// <summary>Reads a detached snapshot for one exact user thread.</summary>
    internal bool TryGetSnapshot(string userId, string threadId, out ChatRunSnapshot? snapshot)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(userId, out var userEntries) ||
                !userEntries.TryGetValue(threadId, out var entry))
            {
                snapshot = null;
                return false;
            }

            IReadOnlyList<ChatDisplayMessage>? messages = entry.SerializedMessages is { } serialized
                ? serialized.Deserialize<List<ChatDisplayMessage>>(_serializerOptions) ?? []
                : null;
            snapshot = new ChatRunSnapshot(
                entry.ThreadId,
                entry.RunId,
                entry.UserMessageId,
                entry.InstanceId,
                entry.Sequence,
                entry.Phase,
                entry.Outcome,
                messages,
                entry.NodeUnavailable,
                entry.McpFailure,
                entry.PermissionPreset,
                entry.Todos is { } todos ? [.. todos] : null,
                entry.ProjectFolderUnavailable);
            return true;
        }
    }

    /// <summary>Subscribes one panel callback to projection signals for a user.</summary>
    internal IDisposable Subscribe(string userId, Func<ChatRunSignal, Task> handler) =>
        _notifications.Subscribe(userId, handler);

    /// <summary>Removes the retained projection for one user thread.</summary>
    internal void Remove(string userId, string threadId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(userId, out var userEntries))
            {
                return;
            }

            userEntries.Remove(threadId);
            if (userEntries.Count == 0)
            {
                _entries.Remove(userId);
            }
        }
    }

    /// <summary>Removes every retained projection for one exact user.</summary>
    internal void RemoveUser(string userId)
    {
        lock (_gate)
        {
            _entries.Remove(userId);
        }
    }

    /// <summary>Signals the untouched metadata-only entry after admission becomes durable.</summary>
    internal void AnnounceStarting(string userId, Entry entry)
    {
        ChatRunSignal? signal = null;
        lock (_gate)
        {
            if (TryGetCurrentEntry(userId, entry, out var current) &&
                !current.StartingAnnounced &&
                current.Sequence == 0 &&
                current.Phase == ChatRunProjectionPhase.Starting &&
                current.SerializedMessages is null)
            {
                current.StartingAnnounced = true;
                signal = CreateSignal(current, ChatRunChangeKind.Structural);
            }
        }

        Notify(userId, signal);
    }

    /// <summary>Publishes a serialized display slice only when the run still owns its entry.</summary>
    internal void Publish(
        string userId,
        Entry entry,
        IReadOnlyList<ChatDisplayMessage> runMessages,
        ChatRunProjectionPhase phase,
        ChatRunChangeKind kind,
        IReadOnlyList<ChatTodoItem>? todos)
    {
        ChatRunSignal? signal = null;
        lock (_gate)
        {
            // A stale handle must never mutate the replacement entry, even when run ids match.
            if (!TryGetCurrentEntry(userId, entry, out var current))
            {
                return;
            }

            current.SerializedMessages = JsonSerializer.SerializeToElement(runMessages, _serializerOptions);
            if (todos is not null) { current.Todos = [.. todos]; }

            current.Phase = phase;
            current.Sequence++;
            signal = CreateSignal(current, kind);
        }

        Notify(userId, signal);
    }

    /// <summary>Completes a run only when its exact entry is still current.</summary>
    internal void Complete(string userId, Entry entry, ChatRunOutcome outcome, bool nodeUnavailable, McpConnectionException? mcpFailure,
        bool projectFolderUnavailable)
    {
        ChatRunSignal? signal = null;
        lock (_gate)
        {
            if (!TryGetCurrentEntry(userId, entry, out var current))
            {
                return;
            }

            current.Phase = ChatRunProjectionPhase.Terminal;
            current.Outcome = outcome;
            current.NodeUnavailable = nodeUnavailable;
            current.McpFailure = mcpFailure;
            current.ProjectFolderUnavailable = projectFolderUnavailable;
            current.Sequence++;
            signal = CreateSignal(current, ChatRunChangeKind.Terminal);
        }

        Notify(userId, signal);
    }

    private bool TryGetCurrentEntry(string userId, Entry expected, out Entry current)
    {
        if (_entries.TryGetValue(userId, out var userEntries) &&
            userEntries.TryGetValue(expected.ThreadId, out current!) &&
            ReferenceEquals(current, expected))
        {
            return true;
        }

        current = null!;
        return false;
    }

    private static ChatRunSignal CreateSignal(Entry entry, ChatRunChangeKind kind) =>
        new(entry.ThreadId, entry.RunId, entry.InstanceId, entry.Sequence, kind);

    private void Notify(string userId, ChatRunSignal? signal)
    {
        if (signal is not null)
        {
            _ = _notifications.NotifyAsync(userId, signal);
        }
    }

    /// <summary>Stores one serialized projection entry behind the store gate.</summary>
    internal sealed class Entry(
        string threadId,
        string runId,
        string userMessageId,
        long instanceId)
    {
        internal string ThreadId { get; } = threadId;
        internal string RunId { get; } = runId;
        internal string UserMessageId { get; } = userMessageId;
        internal long InstanceId { get; } = instanceId;
        internal long Sequence { get; set; }
        internal ChatRunProjectionPhase Phase { get; set; } = ChatRunProjectionPhase.Starting;
        internal ChatRunOutcome Outcome { get; set; }
        internal ChatPermissionPreset? PermissionPreset { get; set; }
        internal bool NodeUnavailable { get; set; }
        internal bool ProjectFolderUnavailable { get; set; }
        internal McpConnectionException? McpFailure { get; set; }
        internal JsonElement? SerializedMessages { get; set; }
        internal IReadOnlyList<ChatTodoItem>? Todos { get; set; }
        internal bool StartingAnnounced { get; set; }
    }
}
