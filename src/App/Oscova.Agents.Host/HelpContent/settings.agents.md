# Agents

Create specialized assistants with their own instructions and access.

> **In plain terms:** each custom agent is configured independently.

## Create and configure

- **Browse prebuilt agents** shows templates from enabled plugins and lets you inspect instructions before installing an editable copy.
- **Agent Templates** comes bundled with Oscova and supplies eight standard starters, available offline after installation.
- **Install agent** saves immediately and opens the editor. Available tools requested by the template are selected. Review or change them in the editor.
- Plugins provide the displayed name, description and setup guidance directly. They may suggest a built-in picture from Oscova's picture catalog.
- Disable/removal takes effect after restart. Created agents and your edits remain available.
- For Researcher or Document Analyst library retrieval, select **Library** under **Workspace Folders** and complete indexing/provider setup.

> **Note:** Installing again creates another copy. Updates preserve your edits. If installation is uncertain, refresh Agents before retrying.

- **New Agent** immediately saves an agent named **Assistant**, adding a number if needed, and opens its editor.
- Keep or change its required name, choose a picture, then set its description and system instruction.
- Insert `{{ user_nickname }}` when the instruction should use the current user's preferred name.

> **Note:** The editor omits the changing current-time variable. Allow a date/time tool when the agent needs the clock.

- **Default Model Provider** is used by tasks left on **Use agent default** and calls from other agents.
- Your own chat with this agent and explicit task providers override it.
- **Use chat model when joining another agent's chat** is off by default. Enable it to follow that web chat's selected model for mentions and same-chat consultations. Tasks and separate delegated work keep this agent's default provider.
- **Use app default** falls back to the app-wide provider.
- Choose compaction, memory, task list, and local-only mode.
- **Use subagents** is on by default. It lets the agent hand independent tasks to up to four separate instances of itself that work in the background. Turning it off leaves **Agent Tools** unchanged.
- **Goal max iterations** is your personal goal-work limit for this agent. Blank uses **Default** (2,147,483,647). Enter a whole number from 1 to 2,147,483,647 to set a smaller ceiling. Budget limits still apply. This setting is excluded from shared and exported agent definitions.
- Select tools, approval modes, agent tools, skills, and file access.
- **Show enabled tools only** filters tools selected in the current draft, together with search and server filters. Select **Save** to persist selection changes. The filter starts off when you reopen the editor.
Select the workspaces and tools the agent may use. Start a chat under a Project to work in an external folder on its selected computer.

Select **tools** under **Workspace Folders** to let this agent read and write reusable scripts and tool projects shared within your account. Custom agents start without this workspace selected. Delegated agents can also use shared folders inherited from their caller, and selected Projects can grant their own access.
- Host Shell appears only when the selected execution provider supports it, and scheduled tasks or delegated agents never receive it.
- Selecting it does not approve calls: the default asks each time, while **Auto Approve** removes that prompt under the saved agent policy.
- **Expose as Tool** makes the agent available for selection by other agents.
- A callable-name conflict must be fixed before the agent can save.
- Leaving the page or using **Back** with unsaved changes asks whether to discard the draft.

> **Note:** In Docker execution, normal commands run in Docker. If an agent is allowed to use Host Shell, it may ask for approval to run a foreground command as your current host user. Use Host Shell only for host-persistent installs or host-only tools.

## Share safely

- Admins and moderators can share an agent with everyone or selected users.
- Other users can only customize their personal context.
- Add `{{ user_context }}` to the shared instruction to use that context.
- Review write tools, credentials, and writable folders before sharing.
- Deleting a custom agent removes its chat and task references for every user, including agents that are or were shared.

> **Note:** personal context needs its own **Save** and should not contain secrets.

A shared agent is available after its owner unlocks their vault. Your personal context is encrypted under your own Vault Password. If access is removed while your vault is locked, Oscova removes the obsolete context after you unlock.

## Share an agent file

- **Export agent** downloads one `.oscagent` file with instructions, selected tools, approval rules, and the agent picture.
- Chats, memory, tasks, and private agent notes are never included.


## Encrypted conversations

- All agents store encrypted chats and task runs, including the main agent and subagents.
- Unlock your **Vault Password** before chatting or running tasks.
- Scheduled tasks wait while the vault is locked and create no run record.
- Daily Memory and Reflection can use encrypted conversations after unlocking.
- Conversation indexing and recall require an unlocked **Vault Password**, **Enable Memory**, and a working embedding provider.
- Select encrypted file readers explicitly to inspect encrypted files or saved long tool output.
- Encrypted files keep their original extension followed by `.enc`, such as `notes.md.enc`.

> **Note:** Agent and task definitions, tool policies, personal context, system prompts and user settings are stored as encrypted files.

## Related

- [Main Agent](settings.main-agent.md) - configure the default agent.
- [Tools](settings.tools.md) - browse available tools.
- [Files](settings.files.md) - manage Library files.
- [Import agent](dialog.agent-import.md) - help for this dialog.
- [Context usage](dialog.context-usage.md) - inspect the current instruction estimate.
- [Choose picture or wallpaper](dialog.image-gallery.md) - change a saved agent's picture.
