# Files

File lists adapt to the available width, with labelled groups on narrow screens.

Files lets you browse, create, upload, edit, download, and delete items in your account workspace.

> **In plain terms:** it is your server-side file area for agent-readable material.

## Manage files

- Recognized Oscova-managed folders show a short purpose description beneath the breadcrumb.
- Ordinary user-created subfolders do not show a system description.
- **Upload** accepts up to 10 files, each no larger than 10 MB. If you select more, an error appears and you can select a smaller batch.
- Click an editable file name or edit icon to open it.
- **Shared** opens the folder every user with the Shared folder permission can use.
- Everyone with access can change or delete what you save in **Shared**.
- Encrypted `.enc` files in **Shared** open for every member without another password.
- **Shared** has no file history and holds no Library documents.

> **Note:** Deleted items have no recycle bin.

Agent and task definitions, tool policies, personal context, system prompts and user settings are stored as encrypted files. Open them in the Files editor to view and edit their decrypted content while your vault is unlocked. Saving a definition updates the app immediately. Creating or saving any `.enc` file in Files encrypts it automatically, without a prompt. The lock marker identifies `.enc` filenames. Existing plaintext `.enc` files become encrypted on their next Files save.

## Find agent-created files

- For coding, use **Add project** in the dashboard with an existing host folder, without changing Agent Editor.
- Project chats use that folder for relative paths. **Files** in chat previews and edits supported writable text.
- Project **Changes** requires Git and shows repository changes, including work done outside the chat.

- A file request without a destination normally uses the agent's writable default workspace.
- Name another workspace or granted folder when you want the file there.
- Agent results can show a workspace address instead of the full server path. The file appears in the selected folder.
- The agent can use only its granted folders. Its private folder belongs to that agent and can contain its own subfolders.
- The **tools** workspace keeps reusable scripts and tool projects across chats. Your main agent starts with read and write access, and custom agents can be granted access in their Workspace Folders selection.

## Use Library Search

- Put `.txt`, `.md`, or text-bearing `.pdf` files in **workspace > library**.
- Create, upload, edit, or delete Library files on this screen to trigger indexing.
- Configure an embedding provider before indexing can start.
- **Ready** files can be searched by agents granted the **Library** workspace.
- **Failed** files stay stopped while their content and embedding provider are unchanged.
- Editing the file or changing the embedding provider triggers normal indexing for the new state.

> **Note:** Library access is read-only. Scanned PDFs need OCR before indexing.

## Related

- [Agents](settings.agents.md) - grant an agent Library access.
- [Model Providers](settings.model-providers.md) - select an embedding provider.
- [Backup](settings.import-export.md) - export a restorable backup.
- [Create file or folder](dialog.create-file-system-item.md) - help for this dialog.
- [Edit file](dialog.file-editor.md) - help for this dialog.
- [File history](dialog.file-backup-history.md) - help for this dialog.
