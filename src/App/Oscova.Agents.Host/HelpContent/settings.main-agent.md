# Main Agent

Configure the agent used when chat or a messaging channel does not select an available custom agent.

> **In plain terms:** these settings define its standing instructions and access.

## Shape behavior

- Set the agent's required name, description, and standing system instruction.
- Insert `{{ user_nickname }}` when the instruction should use your preferred name.

> **Note:** The editor omits the changing current-time variable. Allow a date/time tool when the agent needs the clock.

- **Default Model Provider** is used by messages handled by the main agent and tasks left on **Use agent default**.
- Discord, Telegram, and Slack use this agent by default, but each channel can select an available custom agent.
- The chat provider and explicit task providers override it.
- **Use app default** falls back to the app-wide provider.
- **Use chat model when joining another agent's chat** controls web chat consultations and is off by default. Your own chat with this agent still uses the composer selection. Tasks and separate delegated work use the provider above.
- Compaction makes room in long conversations.
- **Enable Memory** loads saved daily and long-term notes.
- Conversation indexing and recall require an unlocked **Vault Password**, **Enable Memory**, and a working embedding provider.
- **Enable Task List** lets the agent track multi-step chat work.
- **Use subagents** is on by default. It lets the agent hand independent tasks to up to four separate instances of itself that work in the background. Turning it off leaves **Agent Tools** unchanged.
- **Goal max iterations** limits goal work cycles. Blank shows **Default**, allowing completion-driven work up to 2,147,483,647 cycles. A custom whole number can range from 1 to 2,147,483,647. Budget limits still apply. Changes apply to later goal runs.
- **Local only** blocks cloud models and internet tools for this agent.
- The main agent stores encrypted chats and task runs. Unlock your **Vault Password** before using it.

## Choose access

- New main agents start with general-purpose tools selected and **Tool discovery** on.
- New main agents include saved-chat listing without a pin. Existing main agents select it manually under **Allowed Tools**.
- **Allowed Tools** selects callable tools and their approval modes.
- **Show enabled tools only** filters the current selection and works with search and server filters. Clearing a tool removes it from the filtered view. Select **Save** to persist selection changes.
- Tool discovery keeps unpinned selected tools searchable instead of always visible.
- **Agent Tools** lets this agent call exposed custom agents.
- **Skills** selects imported skill packages.
- Workspaces and external directories define file access.
- Name the destination workspace when you want a file outside the default workspace.
- Library access is read-only and adds Library Search.
- **Shared folder** starts selected and grants read and write access to the folder shared by permitted users, at `/shared`.
- **tools** starts selected for the main agent and grants read and write access to reusable scripts and tool projects. Clearing it removes the configured workspace grant, while a selected Project can still grant access to that folder.

Sandbox workspaces use `/workspace/<folder-name>` and `/self`. Node project file tools accept `/project`, while project shell uses the selected computer's native project path. Choose a Project to grant an external folder.

> **Note:** Shell tools accept a listed folder as `workdir`. Commands use relative paths or the listed root environment variables.

> **Note:** click **Save** when the save bar appears. Current replies keep their starting configuration.

Leaving with unsaved editor changes asks whether to discard them.

## Related

- [Agents](settings.agents.md) - create custom agents.
- [Prompts](settings.prompts.md) - edit shared prompt templates.
- [Context usage](dialog.context-usage.md) - inspect the current instruction estimate.
- [Choose picture or wallpaper](dialog.image-gallery.md) - change a saved agent's picture.
