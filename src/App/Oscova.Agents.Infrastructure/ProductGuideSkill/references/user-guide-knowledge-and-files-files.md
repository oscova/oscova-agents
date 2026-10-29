# Files

The Files screen manages your personal server-side workspace in Oscova Agents. Browse folders, create text files, upload files, edit supported documents, download files and delete workspace items.

> **Note:**
> A workspace is a server-side folder area assigned to your account. Agents use workspace material only when their tools and allowed paths permit it.



## Browse folders

Use **Home** and the breadcrumb trail to move through folders. Folders appear before files, and entries are sorted by name. Some top-level folders are protected from deletion, but their contents can still be managed when allowed.

Recognized Oscova-managed folders show a short purpose description beneath the breadcrumb: agent definitions and data, workspaces, settings, prompts, skills, tasks, memory, conversations, logs, metrics, audit records, notifications and tool usage state, plus stable managed subfolders such as agent notes, daily memory, task runs and chat attachments. Ordinary user-created subfolders show no description.

## Create folders and files

- **New Folder** creates a folder in the current location.
- **New File** creates a blank editable file with one of these extensions: `.md`, `.txt`, `.json`, `.yaml`, `.yml`, `.csv`, `.xml` or `.enc`.
- Names must be valid file names. Path traversal and unsafe names are rejected.
- Memory, private-note and to-do concepts need a nonempty `type` in their YAML metadata. Ask an agent to create a concept, or upload a complete formatted document under its `.md.enc` filename. Empty or malformed concepts are rejected before storage.

## Upload files

1. Select **Upload** and choose up to 10 files of 10 MB or less each. If you select more, an error appears and you can choose a smaller batch.
2. Check the result. Uploads overwrite an existing file with the same name in the current folder.

For reference material agents read directly, prefer text formats over large binary files. Uploads are staged through a temporary sibling file and promoted only after the size check succeeds, so a failed or oversized upload does not truncate the existing file.

### Add Library documents

For semantic discovery across reference documents, put supported files in the managed **library** workspace. Oscova indexes that folder separately so an authorized agent can use Library Search even when you do not remember a file name or exact wording.

Manage Library files on this screen: agents get read-only access, and these actions trigger indexing. Direct disk changes are picked up during later reconciliation, including after a Host restart. **Watch My Files** does not monitor Library documents.

## Browse a coding project

Use **Add project** in chat to choose a connected device and name one existing folder there. The operator can also choose the server. Regular users need Projects permission. A server project can use an allowed managed workspace or an external folder. Library, private application storage, filesystem roots and paths containing symbolic links cannot be project roots.

In a project chat, **Files** in Workspace browses the project and the agent's other authorized folders, with the agent's private folder labelled separately. Choosing another preview folder does not change the agent's working directory.

- Each listing shows at most 500 direct entries.
- Supported text previews and edits are limited to 512 KiB and 5,000 lines.
- Writable files offer **Edit**, **Save** and **Cancel**, with conflict handling when another writer changes the file.
- **Changes** uses installed Git to review the repository's staged, unstaged and untracked files. It has no staging, commit or restore controls. Workspace **History** remains the restore-point feature for eligible managed files.

See [Main agent chat](references/user-guide-chat-and-agents-main-agent-chat.md) for project access, delegated approvals, supported Git layouts and encrypted or incognito behavior.

## Grant an agent access

The Files screen manages content. The agent editor controls which folders an agent can use.

1. Under **Workspace Folders**, select each managed workspace the agent may access. Selecting **Library** grants read-only access to its source files and adds the Library Search tools.
2. Under **Additional Folder Access**, choose **External** for an existing host folder or **Managed** for a path inside your user data.
3. Give each additional folder a unique **Label**, then choose **Read Only** or **Read & Write**. In sandbox mode the folder appears to the agent under `/mnt/<label>`.
4. Select **Save**. Changes are not saved automatically, and labels that resolve to the same mount name block saving until they are unique.

Oscova creates a **tools** workspace for reusable scripts and tool projects. Your main agent starts with read and write access. For a custom agent, select **tools** under **Workspace Folders**. Each agent with access reuses the same files across chats. Removing the selection removes its configured grant, though an explicitly selected project or a caller's delegated access can still make the folder available. Runtime instructions show the folders available to that run.

## Edit text files

Select an editable file name or its edit icon to open the built-in editor. It supports the same extensions as **New File** and content up to 512 KB. Larger files can be downloaded but not opened or saved in the browser. Markdown files get variable highlighting, and other formats open as plain text.

- Encrypted text files keep their original extension followed by `.enc`, such as `notes.md.enc`. Files marks these filenames with a lock and encrypts their content automatically when you create or save them, without a prompt. After the Vault Password gate, Files opens encrypted content as readable text. Existing plaintext `.enc` files remain readable and become encrypted on their next Files save. An encrypted file protected by a different key, such as one copied from another account, does not open.
- Downloading an encrypted file returns its ciphertext, not the plaintext shown in the editor.
- Memory, private-note and to-do concepts in the `memory`, agent `notes` and `backlog` folders use YAML metadata followed by Markdown. Keep a nonempty `type` in that metadata. Saving a concept rebuilds its directory indexes. Derived `index.md.enc` files can be read here and are updated through concept edits.
- Agent and task definitions, tool policies, personal context, system prompts and user settings are stored as encrypted files. Open them here to view and edit their decrypted content while your vault is unlocked. Saving a definition updates the app immediately.
- Saving runtime prompt files such as compaction, memory, environment, tool-search and task-list prompts invalidates cached agents, so later runs use the updated content. A response already in progress keeps the prompt it started with.

### File history

When backup history exists for a standard workspace file, the editor shows **History** with the number of saved revisions. Use it to compare or restore earlier text before overwriting an important prompt, skill resource or workspace note.

- Each editable file keeps up to 30 restore points.
- Oscova skips a duplicate restore point when the latest saved version already matches the current file, and restoring an older version first tries to snapshot the current file.
- History is not shown for oversized files, unsupported extensions, directories, missing files or the internal file-history folder.

## Download and delete

- The download icon saves a copy to your computer.
- The delete icon removes a file or folder after confirmation. Deleting a folder removes all contents, and there is no recycle bin.

> **Important:**
> Export a backup before deleting large folders or reorganizing important reference material.

## If an agent cannot see a file

- Confirm the file is in the workspace or an external directory assigned to that agent.
- Confirm the agent has a tool that can read files.
- Confirm the agent's allowed paths include the file location.
- If you just changed a grant, confirm you selected **Save** in the agent editor.
- Confirm the file type and size are practical for the intended tool.
- Manage Library files through this screen. **Watch My Files** does not trigger Library indexing.

Direct file access and Library Search solve different problems. Use a file or PDF tool when the prompt names a specific file. Use Library Search to discover relevant passages across indexed Library files.

## Security behavior

The file API keeps operations inside your user directory and rejects paths that escape through traversal or symlinked segments.

> **Note:**
> A symlink is a filesystem shortcut that points somewhere else. Oscova rejects symlink escape paths so a workspace action cannot silently reach outside your user area.

## Related articles

- [Library search](references/user-guide-knowledge-and-files-library-search.md)
- [Workspaces](references/user-guide-concepts-workspaces.md)
- [Skills](references/user-guide-knowledge-and-files-skills.md)
- [Instructions](references/user-guide-knowledge-and-files-instructions.md)
