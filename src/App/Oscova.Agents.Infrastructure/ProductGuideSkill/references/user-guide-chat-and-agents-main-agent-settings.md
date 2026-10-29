# Main agent settings

Main Agent settings shape the agent used by default in chat and by Discord or Telegram when no custom agent is selected. They cover identity, standing instructions, model choice, memory, tool access, skills and file access.

> **Note:**
> **System instruction** means the standing prompt an agent receives every time it runs. Put durable behavior there, not one-time requests.

## Identity and instructions

Set the required **Name** and optional **Description**, then use **System Instruction** to define the agent's operating style, responsibilities and constraints.



When the main agent uses a supplied persona, its instruction editor offers **Use Default**, **Add to Default** and **Replace Default**. Defaults follow application updates while your custom text and mode stay saved. **Restore default** clears custom text and switches to Use Default after confirmation. Save the instruction editor, then save the Main Agent form. Cancelling the form keeps the saved instruction. See [Instructions](references/user-guide-knowledge-and-files-instructions.md) for the mode behavior.

The editor offers these instruction variables:

- `{{ agent_name }}` for the current agent name.
- `{{ current_date }}` for the current local date.
- `{{ user_name }}`, `{{ user_nickname }}`, `{{ user_username }}` and `{{ user_email }}` for the current user.
- `{{ user_timezone }}` for the user's time zone.

When the agent needs the current time, allow its date/time tool instead of placing a changing time value in its standing instruction. This preserves prompt-cache reuse for local models.

To change the picture, select **Change profile picture**, choose a built-in picture or upload a PNG, JPEG or WebP file up to 2 MB, then select **Use this picture**. The picture saves separately from the Main Agent save bar.

### Check context usage

**Context usage** beside the system instruction estimates the prompt the current editor settings would produce. The dialog shows **Estimated initial input tokens**, the resolved instruction contributors and tool declarations grouped by toolset or MCP server. Expand a tool to inspect its description and parameter schema. Close the dialog, change a setting and reopen it to compare.

- With tool discovery on, pinned tools and discovery count initially. Unpinned searchable tools appear under **Deferred tools**, outside that total. Skills contribute discovery text and loader tools initially, while their full content loads later.
- The estimate excludes conversation input, later tool results and loaded skill bodies. Model tokenization and request formatting can differ. Context capacity and reserved output are shown separately when configured.
- Previewing agent tools requires an available model provider.

## Provider, memory and compaction

**Default Model Provider** is used when a main-agent run does not choose a provider. The chat provider picker and a task's explicit provider selection override it, messaging channels use the selected agent's default, and tasks left on **Use agent default** use their agent's default. **Use app default** falls back to the app-wide provider.

**Use chat model when joining another agent's chat** is off by default and controls web chat consultations. When enabled, this agent follows that chat's selected provider when consulted there. Your own chat with this agent still follows its composer selection, and tasks, channels and delegated work keep their own provider choices.

- **Enable Compaction** summarizes long conversations when the context window needs room.
- **Enable Memory** lets the agent load and save memory across sessions.
- **Enable Task List** lets the agent maintain a task list during complex chat work.
- **Use subagents**, on by default, lets the agent hand independent tasks to up to four separate instances of itself in the background. Turning it off leaves **Agent Tools** unchanged.
- **Goal max iterations** limits goal work cycles. Blank shows **Default**, which allows up to 2,147,483,647 cycles. Set a whole number from 1 to 2,147,483,647 for a custom ceiling. Existing budgets still apply, changes take effect on later goal runs, and Resume starts a fresh iteration allowance while keeping progress and elapsed time.
- **Local only** blocks this agent from cloud model providers and internet tools even when global Offline mode is off. Local models, loopback and LAN endpoints, local tools and local embedding providers still work.

The main agent stores encrypted chats and task runs by default. Unlock your Vault Password before running it.

> **Important:**
> Conversation indexing and recall require an unlocked Vault Password and a working embedding provider. Daily and long-term memory notes do not need embeddings. See [Chat memory](references/user-guide-chat-and-agents-chat-memory.md) for the distinction.

## Tools, skills and files

**Allowed Tools** controls which ordinary callable tools the agent may use. New main agents start with Oscova's general-purpose built-in tools selected, the **default** workspace granted and tool discovery on. Oscova also assigns its product-guide skill so the assistant can consult guidance about the app. Existing agents keep their saved selections.

For each selected tool, pin it to keep it visible when discovery is on and choose an approval mode:

- **Default** follows the tool's normal policy.
- **Require approval** asks before the call proceeds in interactive chat.
- **Auto-approve** allows the call without prompting.
- **Deny** keeps the tool selected but blocks this agent from using it.

Other controls:

- **Show enabled tools only** filters the table to the tools in the current draft and works with search and server filters. Clearing a tool removes its row while the filter is on. The filter starts off whenever the editor opens.
- Tool discovery keeps large tool sets out of the prompt until needed. Pinned tools are visible immediately and the agent searches for the rest. Turning discovery off keeps saved pins but exposes all selected tools.
- Saved-chat listing and reading are selected for new main agents and not pinned. Existing agents can select `chathistory__list` and `chathistory__read` manually. They search your saved titles and messages across agents and projects while your Vault Password is unlocked, including conversations without indexed memory, and excludes incognito. See [Find a saved chat](references/user-guide-chat-and-agents-main-agent-chat.md).
- **Agent Tools** are other exposed agents this agent can call. **Skills** are imported skill packages assigned to this agent.

### Workspace folders

**Workspace Folders** chooses the server workspaces the agent can access. Select a Project in chat to grant an external folder on its selected computer.

- The **tools** workspace starts selected for a new main agent and grants read and write access to reusable scripts and tool projects in your account. Clearing it removes that configured grant, though an explicitly selected project can still grant the same folder. Runtime instructions reflect the folders available to that run.
- Selected workspaces appear under `/workspace/<folder-name>` and agent private files under `/self` in every mode. Project files use `/project`. Shell commands select a folder through `workdir` and use relative paths or the listed root environment variables on the selected computer.
- Selecting **Library** grants read-only access to that workspace and adds `library__search` plus `library__get_index_status`. Manage Library content through **Settings > Files** so changes trigger indexing.

## Save behavior

Changes are not saved automatically. The save bar appears after a draft changes, and leaving with unsaved editor changes asks whether to discard them. After saving, future chat turns and task runs use the new configuration, while a response already in progress keeps the configuration it started with.

Discord and Telegram can each select an available custom agent. Unlock your Vault Password before channel execution. If the selection later becomes unavailable, the channel uses your main agent on its next message. If saving reports an unavailable provider, choose an available provider and save again.

## Related articles

- [Main agent chat](references/user-guide-chat-and-agents-main-agent-chat.md)
- [Model providers](references/user-guide-concepts-model-providers.md)
- [Library search](references/user-guide-knowledge-and-files-library-search.md)
- [Skills](references/user-guide-knowledge-and-files-skills.md)
- [Tool governance](references/user-guide-concepts-tool-governance.md)
