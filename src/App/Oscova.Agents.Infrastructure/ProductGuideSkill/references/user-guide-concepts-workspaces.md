# Workspaces

Workspaces give agents a named, reviewable view of files. An agent in Oscova Agents sees only the managed workspaces and additional folders granted in its settings or in a task run scope.

> **Note:**
> A managed workspace is a folder inside your private Oscova file area. It is not your whole user-data folder or your browser downloads folder.

## Understand the file-access sources

An agent run can receive four kinds of file access:

| Source | How it is granted | Access |
| --- | --- | --- |
| Managed workspace | Select it under **Workspace Folders** | Read and write, except Library |
| Agent private folder | Added automatically for that agent | Read and write |
| Additional folder | Add it under **Additional Folder Access** | Your selected mode |
| Run attachment | Attach it to the current chat | Temporary read-only access |

- New main agents start with the **default** workspace selected. Other agents and custom task scopes use their own saved selections, so review their grants separately. Existing agents keep their saved workspace choices.
- The agent's private folder stores that agent's working documents, notes and progress records.
- The managed **Library** workspace is always read-only to agents. Selecting it also enables Library Search. Manage Library files through **Settings > Files** so indexing stays synchronized.
- Additional folders are **External** (an existing folder on the Oscova host) or **Managed** (a folder inside your private Oscova user area). Give every additional folder a label and choose **Read Only** or **Read & Write**. Prefer read-only for reference material.

## Use agent-facing paths

File tools accept these stable aliases in every execution mode:

- Managed workspaces appear as `/workspace/<name>`.
- The agent's private folder appears as `/self`.
- Additional folders appear as `/mnt/<label>`.
- The [shared folder](references/user-guide-knowledge-and-files-shared-folder.md) appears as `/shared` when the agent and its user have access.

Labels and workspace names are normalized for mount paths: repeated punctuation becomes a hyphen and letters become lowercase. Saving is blocked when two entries under the same mount root normalize to the same path.

Oscova also rejects a configuration when a stable path could mean a different native host folder. On Linux, for example, an additional grant for a physical `/self` folder conflicts with the agent private folder that uses the `/self` stable path. Choose a different physical folder or remove the conflicting grant. Renaming its label does not change the physical conflict.

- Workspace Access, directory listings, search and Library results use the stable paths above in every execution mode. Each directory entry includes its complete path, so it remains usable when a page omits its parent folders.
- For shell commands, the agent selects a folder through `workdir` and uses relative paths or the root environment variables shown in its environment context.
- For a project on another computer, file and directory tools route granted workspace and private aliases to the server and `/project` to the project computer. Relative paths use the selected project by default. The project computer's shell cannot access server workspace paths.
- A request with only a filename uses the run's writable working directory, normally the selected default workspace or the first writable grant. Name the destination when you want another location. For example, `self/drafts/outline.md` goes in the current agent's private `drafts` folder, while `workspace/research/outline.md` goes in your research workspace.

File tools run in the host process, resolve the agent-facing path to an approved host path and perform host file operations there. Container-sandboxed shell commands see the same files through bind mounts. A Docker sandbox therefore isolates command execution, while path grants remain the file-access boundary shared by both surfaces.

MCP servers have their own execution environment and permissions. Review the server configuration as well as the agent's tool selection before trusting an MCP tool with files.

## Know what containment blocks

Oscova checks file-tool paths again when the tool is invoked. It rejects:

- paths outside the run's approved directories.
- `..` traversal that resolves outside a grant.
- symlink or reparse-point hops beneath an approved root.
- writes inside a read-only grant.

When grants overlap, the most specific matching directory controls access. If the same path has conflicting modes, read-only wins.

This enforcement does not make every tool safe. Host shell commands are not confined by file-tool grants and keep Oscova's operating-system account permissions. A tool must mark its file parameters for workspace validation, and commands or external programs keep the permissions of their configured execution environment.

## Edit files in chat

Open **Workspace** in chat and select a file through **Files** or **Artifacts**. Supported writable text offers **Edit**, with explicit **Save** and **Cancel**.

- Saving uses current folder permissions and the original computer, including connected node projects. Read-only grants remain read-only. Successfully decrypted `.enc` text stays encrypted after saving.
- Files and artifacts share document tabs in Workspace. Hiding the panel or switching tabs retains unsaved drafts. Closing a dirty tab or replacing the chat or file-root scope asks you to save, discard or cancel.
- If another writer changed a file, you can explicitly overwrite it or confirm reloading the latest text. See [Edit a workspace file](references/user-guide-chat-and-agents-main-agent-chat.md) for limits and recovery.

## Troubleshoot file access

- Confirm the workspace or folder is selected for the current agent or task scope, and save the agent after changing grants.
- Confirm the agent has an appropriate file or PDF tool.
- Use the agent-facing path shown for the current execution mode.
- Check whether the grant is read-only before requesting a write.
- Rename labels that collide after mount-name normalization.
- If the run reports a native path conflict, remove the conflicting grant or choose a different physical folder.
- For Library search problems, manage files through **Settings > Files** and check indexing status.
- For MCP file problems, review that server's host or Docker execution environment.

## Related articles

- [Files](references/user-guide-knowledge-and-files-files.md)
- [Library search](references/user-guide-knowledge-and-files-library-search.md)
- [Shared folder](references/user-guide-knowledge-and-files-shared-folder.md)
- [Main agent settings](references/user-guide-chat-and-agents-main-agent-settings.md)
- [MCP servers](references/user-guide-connections-mcp-servers.md)
