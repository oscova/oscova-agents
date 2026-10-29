# Conversation persistence

Use IChatThreadRepository as the conversation persistence boundary. Its file-backed ChatThreadRepository implementation stores thread summaries, display transcripts, and serialized framework session state beneath a users root selected by your host.

The repository belongs to the reusable `Oscova.Agents` package. Your host remains responsible for user identity, authorization, path policy, dependency lifetimes, and choosing the users root.

## Construct the repository

Supply a root resolver, one shared FileSessionIndexCoordinator, an IRedactionService, an IContentProtector, and a logger. The resolver is evaluated when an operation derives a path, so a host can change its active root without rebuilding the repository.

```csharp
using Microsoft.Extensions.Logging;
using Oscova.Agents.Conversations;
using Oscova.Agents.Persistence;
using Oscova.Agents.Secrets;

var repository = new ChatThreadRepository(
    () => usersRoot,
    new FileSessionIndexCoordinator(),
    redactionService,
    contentProtector,
    logger);
```

Use one coordinator for repositories that can update the same users root. The redaction service masks persisted instructions, message text, tool arguments, and tool results according to host policy. The protector encrypts every stored conversation record under its owner and storage purpose. The host owns the key lifecycle behind that interface.

## Use the conversation contract

`IChatThreadRepository` supports these operations:

- list, load, create, update, and delete ChatThread records.
- rename an existing thread with `TryRenameThreadAsync`, without creating a missing thread.
- list in-memory incognito threads with `GetIncognitoThreadsAsync`.
- save, load, and delete serialized framework session state.
- mark or clear the current thread for an agent.
- update indexing, summary, and reflection watermarks.
- delete all threads for a user or agent.

`GetThreadAsync` returns complete display history by default. Supply a positive `messageLimit` for a display page, optionally with `beforeMessageId` to read an older page or `firstMessageId` to refresh an existing window. The cursors require a limit and cannot be combined. Pages preserve complete exchanges, so their message count can exceed the target. Display paging does not trim the serialized model session. `HasOlderDisplayMessages` reports whether earlier display history remains. A missing cursor is an error requiring the caller to reopen the conversation.

Pass the caller's cancellation token to repository methods. Asynchronous mutations pass it through index coordination, redaction, and file writes. Local file reads are synchronous, so the token does not interrupt every individual read.

`TryRenameThreadAsync` returns `false` for a missing thread or a locked encrypted record. `SaveSessionElementAsync` updates only an existing persisted thread, so a late session save cannot recreate a deleted conversation. Every stored session is encrypted.

## Keep incognito state in memory

Creating a `ChatThread` with `IsIncognito = true` retains it in this repository instance and never writes its transcript, index row, or session sidecar. It is never marked current. While the vault is unlocked, `GetThreadAsync` checks this in-memory state first. A complete read returns the retained instance, while a paged read returns a display-window copy. `GetThreadsAsync` lists persisted threads only. Use `GetIncognitoThreadsAsync` for the separate incognito list.

Thread deletion and user- or agent-wide deletion remove matching incognito state too. Keep the repository alive for the intended session lifetime. A restart or a new repository instance loses its in-memory incognito threads.

## Coordinate conversation runs

Use ThreadRunGuard when every transport must share one active run for an exact user and case-insensitive thread identity. `Acquire` admits immediately or returns `null`, while `AcquireAsync` waits until the current run or user-wide block releases. Dispose the returned run lease to release the slot.

`Cancel` stops the current run, `CancelExact` stops only the matching run, and `CancelOrDefer` preserves an exact cancellation that arrives before admission. `CancelAndAcquireUserAsync` cancels and drains all active runs for one user, then blocks new admissions until its user lease is disposed.

```csharp
using Oscova.Agents.Conversations;

using var lease = runGuard.Acquire(userId, threadId, runId);
if (lease is null)
{
    // The host decides whether to reject the request or wait with AcquireAsync.
    return;
}

await agent.RunAsync(message, session, cancellationToken: lease.CancellationToken);
```

Your host remains responsible for authorization, user identity, transport behavior, service lifetime, and deciding whether a competing run is rejected or waits.

## Understand the disk layout

For a users root such as `/app/users`, the repository uses this stable layout:

```text
<users root>/
  <user id>/
    chat/
      index.jsonl
      <thread id>.jsonl
      <thread id>.session.json.enc
```

`index.jsonl` contains thread summaries. Each `<thread id>.jsonl` file contains the display transcript, while the optional `.session.json.enc` sidecar contains serialized framework session state.

Every persisted conversation uses encrypted index rows, transcript records and sessions. Complete records are protected under the owner and storage purpose. GetThreadsAsync(userId, ct) returns no persisted summaries while locked. CreateThreadAsync creates a new thread. SaveThreadAsync updates an existing thread without recreating deleted state. Hosts keep a draft unpersisted until its project and incognito state are selected.

Persist channel values with ChatChannels: `web_chat` for the built-in web surface and `channel` for plugin channels. The plugin channel ID is recorded separately as `channel_id` in channel hook payloads. These values are stable runtime identifiers rather than product UI labels.

## Own identity and path policy in the host

The repository requires user and thread IDs to be single nonempty path segments. It rejects rooted paths, traversal names, invalid filename characters, and symlink or reparse-point hops beneath the trusted users root. Index, transcript, and session files must remain contained in the selected user's chat directory.

These checks do not authenticate callers. Authorize each operation and apply your product's identity and root-selection policy before calling the repository. User identity is exact, while thread lookup uses case-insensitive identity and retains the stored spelling for its sidecars.

A null root resolver fails at construction. Invalid thread paths, file-system failures, redaction failures, protection failures, and cancellation propagate to the caller. Malformed decrypted index and transcript records are logged and skipped. Unreadable encrypted envelopes and corrupt session payloads fail the read. Use the supplied logger to diagnose those records.

## Related API

- IChatThreadRepository
- ChatThreadRepository
- ChatThread
- ChatChannels
- ThreadRunGuard
- FileSessionIndexCoordinator
- IRedactionService
- IContentProtector
