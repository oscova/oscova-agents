# Chat Memory Store

Records fit the dialog width and become labelled groups on narrow screens. Agent names and thread titles identify each row when available. Expand a record to read its full content and diagnostic identifiers.

Browse conversation chunks that memory-enabled agents can recall in later chats.

> **In plain terms:** a memory chunk is a small saved section of conversation.

## Browse entries

- Entries load when you first open the dialog.
- Filter by agent or thread, and changing a filter returns to page 1.
- Click **Refresh** to reload filters and the current page.
- Use the arrow buttons to move through pages of up to 50 entries.
- Expand a row to read its full content and local timestamp.

## Understand what appears

- Long-term concepts, daily memory and private agent lessons are separate encrypted Markdown files, visible in Files while your vault is unlocked.
- Chat memory needs **Enable Memory**, a working embedding provider, and an unlocked **Vault Password**.
- New conversations are indexed by **Vector Indexing**, enabled by default every 60 minutes. Your saved task settings control when it runs.
- Missing indexes rebuild at the next eligible task run. Chat memory stays encrypted when the app is closed.
- Local-only and Offline rules also apply to embedding calls.
- Deleting a thread removes its indexed chat-memory chunks.

> **Note:** this browser is read-only. Disabling memory stops future use and indexing but does not erase existing chunks.

## Related

- [Model Providers](settings.model-providers.md) - configure the embedding provider.
- [Main Agent](settings.main-agent.md) - enable or disable memory.
