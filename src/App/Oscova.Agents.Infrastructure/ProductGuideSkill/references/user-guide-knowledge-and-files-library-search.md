# Library search

Library Search lets an authorized agent find relevant passages across a durable collection of reference files. Use it when you know the information exists but do not remember the file name, location or exact wording.

> **Note:**
> The Library workspace contains your source files. Its vector index is a rebuildable search aid derived from those files, not a second editable copy.

## Add files to Library

Open **Settings > Files**, then browse to **workspace > library**. Create, upload, edit or delete files there as you would in another workspace.

- Library indexes `.txt` files, `.md` files and text-bearing `.pdf` files.
- Each file must be 10 MB or smaller.
- Scanned or image-only PDFs are skipped because Library does not perform OCR. Convert those scans to searchable text before adding them.

Each create, upload, edit or delete in **Settings > Files** asks Library to reconcile the folder's current contents promptly. An update replaces the complete old index for that file, and deleting a source removes its indexed passages.

Library does not watch for changes made directly on disk. Those changes are picked up at the next reconciliation, including when Library indexing starts after a host restart. Manage files through **Settings > Files** to queue reconciliation immediately.

## Configure embeddings

Library needs a selected embedding provider to index additions and updates or to search. Existing files remain in place when no eligible provider is available and are discovered after one is selected.

- A local provider keeps extracted text on the local endpoint.
- A cloud provider receives extracted document text for indexing and receives search queries.
- Global Offline mode blocks cloud embedding during background indexing.
- Agent-level local-only policy blocks cloud embedding for that agent's Library searches.
- A missing, invalid, unavailable or blocked provider starts no addition or update indexing work.

**Settings > Files** shows the active provider behavior while you browse Library. No extra cloud-consent dialog is required, so review the warning before placing private documents in Library.

## Understand file status

- **Ready:** the current indexed version can be searched.
- **Queued:** the file is waiting for indexing.
- **Indexing:** the file is being processed.
- **Skipped:** the file is unsupported, too large, empty, encrypted or has no extractable text.
- **Failed:** indexing could not finish for the current file and embedding provider.

Queued and indexing rows update automatically while the Library folder remains open. Library Search may report incomplete results while files are queued, indexing, failed or rebuilding after an embedding-provider change.

- Changing the selected embedding provider or its vector dimensions rebuilds the Library index. Old vectors are excluded from results during the rebuild so content from incompatible embedding spaces is not mixed.
- When a reconciliation batch finishes, Oscova Agents sends a notification. A failed batch sends a warning that directs you back to **Settings > Files > Library**.

## Grant an agent access

In the agent editor, select the **Library** workspace. This authorization adds the read-only `library__search` and `library__get_index_status` tools for that agent.

- Selecting Library grants read-only access to its source files. Agents can inspect the live files but cannot create, edit, move or delete them.
- In sandbox mode the path is `/workspace/library`.
- Library Search is on demand. Oscova does not place Library content into every model request. The agent calls the search tool when it needs semantic discovery across the collection.

## Choose the right source

- Use direct file or PDF tools when you name or attach a specific file. Those tools read the live source and are best for exact inspection.
- Use `library__search` when the answer could be spread across files or the prompt does not identify a source. Results include the Library path and, for PDFs, a one-based page number. Verify precise claims against the live source file.
- The tool returns up to 5 results by default. An agent may request more, up to 50 results per search.
- Use `memory__search` for relevant past conversation chunks. Conversation memory and Library files have separate indexes and purposes.

## Troubleshooting

- Confirm an embedding provider is selected and available.
- If local-only policy is active, select a local embedding provider.
- Check the file status in **Settings > Files > Library**.
- Failed files are not retried while their content and embedding-provider configuration are unchanged. Correct the file content or change the provider configuration after fixing the failure, then let indexing reconcile the new state.
- Convert scanned PDFs with OCR before adding them again.
- Confirm the active agent has the Library workspace selected.
- Make Library changes through **Settings > Files**, not through an agent.

## Related articles

- [Files](references/user-guide-knowledge-and-files-files.md)
- [Workspaces](references/user-guide-concepts-workspaces.md)
- [Main agent settings](references/user-guide-chat-and-agents-main-agent-settings.md)
- [Chat memory](references/user-guide-chat-and-agents-chat-memory.md)
