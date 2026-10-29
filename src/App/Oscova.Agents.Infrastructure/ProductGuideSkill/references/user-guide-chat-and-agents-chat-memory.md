# Chat memory

Oscova Agents keeps three kinds of agent memory, all encrypted under your Vault Password: indexed conversation chunks for semantic recall, explicit daily and long-term notes that agents save, and private lessons each agent keeps about its own methods. Daily Memory and Reflection can read your encrypted conversations while the vault is unlocked. Incognito chats remain excluded from learning.

> **Note:**
> An **embedding provider** turns text into numbers for meaning-based search. Conversation recall needs one. Daily and long-term notes do not.

## Requirements

- Every memory feature requires **Enable Memory** on the agent, which gives it its encrypted memory context and tools, and an unlocked Vault Password.
- Conversation recall also requires a selected embedding provider and an indexing run.
- Compaction is separate: it summarizes the current thread, while memory stores reusable facts for later sessions.

## No embedding provider

Without an embedding provider, the **Vector Indexing** task skips its runs and agents cannot search your past chats by meaning. Daily memory, Dreaming and saved memory notes keep working.

To add one, open **Settings** > **Agents & AI** > **Model Providers** and select **Add embedding provider**. See [Embedding provider](references/user-guide-concepts-model-providers.md) for the editor fields. The next **Vector Indexing** run indexes your saved chats.

## Browse stored memory

Open **Chat Memory** from the profile menu to browse indexed conversation chunks.

- The **Vector Indexing** task is enabled by default and runs every 60 minutes on a new installation. Your saved schedule and enabled setting remain in control. New conversations and missing indexes become available after the next eligible run.
- The memory browser is read-only. It does not expose manual edit, bulk delete or export.
- Daily and long-term memory notes remain available through their dedicated memory tools while the vault is unlocked.

## Find a saved conversation

Saved-chat listing is separate from indexed memory. An agent with `chathistory__list` and `chathistory__read` selected can find titles and matching excerpts from your saved conversations, then read the surrounding messages, while your Vault Password is unlocked, without Enable Memory or an embedding provider. See [Find a saved chat](references/user-guide-chat-and-agents-main-agent-chat.md).

## Memory tools agents may use

When memory is enabled, the agent can use these tools during a run:

- `memory__search` finds relevant indexed conversations belonging to your user.
- `memory__get` reads indexed chunks from a specified thread belonging to your user.
- `memory__find` discovers saved user facts, preferences and decisions.
- `memory__read` reads the entry index or a discovered concept or directory index.
- `memory__save` saves one long-term concept with a title, description and structured metadata.
- `memory__save_daily` appends temporary or short-term information to today's encrypted daily log.
- `memory__read_daily` reads today or a specified local date.
- `memory__forget` asks for confirmation before deleting one discovered long-term concept, or every long-term concept when you ask the agent to forget everything about you. Daily memory is kept. Linked details are retained, and remaining links to a deleted concept are reported.

Chat-memory chunks come from conversation indexing for semantic recall. Daily and long-term memory are explicit notes the agent saves and reads through memory tools.

- For a directly run memory-enabled agent, Oscova loads the long-term entry index and today's and yesterday's daily logs into context. Directory indexes point to detailed concepts, which the agent reads when needed.
- Each loaded section holds at most 8 KB. A longer entry index keeps its first entries and tells the agent how many more to find. A longer daily log keeps its newest entries and tells the agent to read the rest. Saved memory is never shortened.
- The model's context window and the 512 KiB limit per complete document still apply.
- These notes belong to your user, so they can help more than one agent.
- Library Search is separate. `memory__search` and `memory__get` use indexed conversations, while `library__search` looks only in indexed files under the authorized Library workspace. Oscova does not automatically inject Library passages into every request. The agent calls Library Search when the question needs document evidence.

## Private agent lessons

Private agents also retain behavioral lessons about successful methods and corrections. Ordinary chats and the Reflection task can save these through notes find, read and save.

- Agents are instructed to check existing lessons before writing and skip unchanged duplicates.
- These lessons belong to that agent, while user facts and preferences belong in user memory.
- `notes__forget` deletes one lesson, or every lesson when you ask the agent to clear its notes, after you confirm.
- The loaded lesson index also holds at most 8 KB and names how many more lessons the agent can find.

## To-do list

Ask any agent to mark something down as a to-do, such as "mark this down as a to-do: renew the domain". The item is yours by default. Say "you do this tonight" to delegate it to the agent, so the Night Shift task works it overnight and the Morning Brief reports the result.

- `backlog__save` adds an item or updates its title, owner, description or state. Say you no longer need an item to withdraw it.
- `backlog__list` shows open items newest first, 20 at a time. Ask for done, withdrawn or all items to see the others.
- `backlog__complete` marks an open item done with one line saying what was done.
- `backlog__remove` deletes one item, every done item or every item after you confirm.
- Each item is an encrypted document in the `backlog` folder in Files. Morning Brief removes done items older than 30 days.
- The Files section shows decrypted Markdown while your vault is unlocked. Long-term concepts, daily records, private lessons, to-do items and their directory indexes are encrypted on disk. Edit a concept to update its derived index.

## What to store

Ask agents to remember durable preferences, naming conventions, recurring project facts and decisions that should survive a chat. Avoid storing secrets, one-time instructions or large source files. Use workspaces or files for reference documents.

## Troubleshooting recall

- Unlock your Vault Password and confirm **Enable Memory** is on. Daily and long-term notes need only these two.
- Check that the embedding provider works. Local-only and Offline restrictions also apply to embeddings.
- Check the **Vector Indexing** task and its last run. A disabled task stays disabled, including when an index needs rebuilding.
- If an index is missing, the next eligible task run rebuilds it from saved conversations. Authentication failures are reported and preserve the existing files.
- An environment backup preserves encrypted indexes and the wrapped vault key, so the same Vault Password unlocks them after restore.

## Related articles

- [Library search](references/user-guide-knowledge-and-files-library-search.md)
- [Model providers](references/user-guide-concepts-model-providers.md)
- [Main agent settings](references/user-guide-chat-and-agents-main-agent-settings.md)
- [Backup and restore](references/user-guide-knowledge-and-files-backup-and-restore.md)
