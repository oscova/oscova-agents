# Custom agents

A custom agent is a specialized AI agent with its own instructions, model behavior, tools, skills, memory settings and file access. Create one when the main agent is too broad for a job, such as editing, research or email triage. Agent definitions and their conversations are encrypted under their owner's Vault Password.



## Create an agent

1. Open **Agents** settings and select **New Agent**. Oscova saves an agent named **Assistant**, adding a number if needed, and opens its editor.
2. Keep or change the required name, then fill in the description and system instruction.
3. Choose a built-in profile picture or upload a PNG, JPEG or WebP image up to 2 MB.
4. Review tools, skills and folder access, then select **Save**.

If saving reports a callable-name conflict, choose a name that normalizes differently from every other custom agent.

### Check the context cost

**Context usage** beside the system instruction estimates what the current editor settings send on every request. The dialog shows **Estimated initial input tokens**, the instruction contributors and the tool declarations grouped by toolset or MCP server. Expand a tool to read its description and parameter schema. Close the dialog, change a setting and reopen it to compare.

- With tool discovery on, pinned tools and the discovery tool count. Unpinned tools appear under **Deferred tools**, outside the total. Skills count their discovery text and loader tools, while their full content loads later.
- The estimate excludes conversation input, later tool results and loaded skill bodies. Model tokenization can differ. Context capacity and reserved output are shown separately when configured.
- Previewing tools needs an available model provider.

## Install a prebuilt agent

The bundled **Agent Templates** plugin supplies eight starters, available offline. Other enabled plugins add templates to the same catalog.

1. Open **Agents** settings and choose **Browse prebuilt agents**.
2. Select a starter, read its setup guidance and expand **System Instruction** to inspect its instructions. **Cancel** closes the catalog without creating anything.
3. Select **Install agent**. Oscova creates an editable agent and opens its editor.
4. Review the selected tools and the setup guidance, then enable extra capabilities only when needed.

| Starter | What to bring and what it helps produce |
|---|---|
| Researcher | A question and sources, for evidence-based investigation and comparison. |
| Study Coach | A topic or learning material, for explanations, practice and quizzes. |
| Interview Coach | A target role or practice answer, for interview practice and specific feedback. |
| Writing/Brand Editor | Text and an optional style guide, for revisions that preserve meaning and voice. |
| Document Analyst | Documents or pasted passages, for cited answers, extraction and version comparisons. |
| Meeting Analyst | Notes or a transcript, for supported decisions, commitments and open questions. |
| Planning Assistant | Goals, commitments and constraints, for realistic priorities and next actions. |
| Email Assistant | Messages or threads, for summaries, triage and reply drafts. |

What a starter installs:

- Each template requests tools. Oscova selects the requested tools that are available, omits missing ones and keeps its normal execution and approval rules. Templates never install providers or connect accounts.
- Starters use **Use app default** for the model, with the chat selection taking precedence. Memory starts off, compaction stays on, and only the default workspace is selected.
- Selected tools read only. No file writing, shell commands, message sending or calendar and task changes. Installation creates no scheduled work, attends no meetings and performs no external actions.
- All starters work with pasted text, and readable text and PDF attachments can use the file tools. Starter text is English and your installed copy is editable.

Starter-specific setup:

- **Researcher:** supplied URLs can use web fetching. Open-web discovery needs a search tool enabled in the editor, from the Web plugin or your search provider.
- **Researcher and Document Analyst:** to search indexed Library material, select **Library** under **Workspace Folders** and complete indexing and provider setup.
- **Planning Assistant:** optional Google Calendar and Microsoft To Do reads need those connected accounts.
- **Email Assistant:** paste messages, or connect Google or Microsoft for inbox reads. Replies stay as text drafts in chat.

How installed templates behave:

- Installing the same starter again creates another agent with a unique name.
- The instruction editor of an installed template offers **Use Default**, **Add to Default** and **Replace Default**. Template updates refresh the default while your custom text, mode and other settings are kept. **Restore default** clears custom text after confirmation. Save the instruction editor, then save the agent form. Cancelling the form keeps the saved instruction. Agents created with New Agent use plain instruction editing.
- Disabling or removing the template plugin and restarting removes its catalog entries. Saved agents survive, and a saved default stays usable.
- An exported agent contains the complete selected instruction. Importing it creates an ordinary custom agent that no longer follows template updates. Runtime and personal context are not part of that instruction.

If the catalog fails to load, select **Retry**. If installation cannot be confirmed, refresh **Agents** and check for a new copy before installing again. If installation succeeded but the editor failed to load, refresh **Agents** and open the saved copy.

## Configure behavior

Custom agents use the same controls as the main agent. Review each agent as its own assistant, because it does not inherit main-agent settings.

- **Default Model Provider** is used by tasks left on **Use agent default** and by delegated work from other agents. Your own chat with this agent and explicit task selections override it. **Use app default** falls back to the app-wide provider.
- **Use chat model when joining another agent's chat** is off by default. Off, mentions and same-chat consultations use this agent's default provider. On, they follow the chat's selected provider. Tasks, messaging channels and separate delegated work keep their own provider choices.
- **Enable Compaction** summarizes long conversations.
- **Enable Memory** supplies daily and long-term memory notes and tools. Searching indexed past conversations also needs an embedding provider.
- **Enable Task List** lets the agent track multi-step work in chat.
- **Use subagents**, on by default, lets the agent hand independent tasks to up to four separate instances of itself in the background. Turning it off leaves **Agent Tools** unchanged.
- **Goal max iterations** is your personal ceiling for goal work. Blank **Default** allows up to 2,147,483,647 cycles. Custom values are whole numbers from 1 to 2,147,483,647. Budgets still apply. The value is excluded from shared and exported definitions.
- **Local only** blocks cloud model providers and internet tools for this agent.
- **Tool discovery** keeps unpinned selected tools deferred until the agent searches for them. With discovery on, you can pin tools so they are visible immediately. Turning discovery off keeps saved pins and exposes every selected tool up front.
- **Allowed Tools**, **Agent Tools** and **Skills** choose capabilities. **Show enabled tools only** filters the current selection together with search and server filters, and starts off each time you open the editor. Select **Save** to keep selection changes.
- **Workspace Folders** chooses server workspace access. Select **tools** to grant read and write access to reusable scripts and tool projects in your account. New agents start without it.
- Projects grant read and write access to their folder when you select one in chat. The operator controls whether regular users can use projects and the project shell.

A shared agent is available after its owner unlocks their vault. Your personal context with it is encrypted under your own Vault Password. If access is removed while your vault is locked, Oscova removes the obsolete context after you unlock.

## Export or import an agent

Select the download action on an agent card to create one `.oscagent` file. It contains the instructions, selected tools, tool approval rules, skills, compatible workspace names, behavior settings and profile picture. It never includes chats, memory, tasks, private notes, ownership, sharing or direct folder grants, and import removes subagent links.

To import, select **Import** and choose a package up to 5 MB. Keep its saved name or enter another. Import always creates a new agent, adding a number when the name is in use. The receiving installation keeps only the tools, workspaces and model providers it has, removes other references and reports how many it removed. Select a Project when the imported agent needs an external folder.

## Encrypted conversations

Every agent, including the main agent and subagents, stores encrypted conversations and task runs. Your Vault Password unlocks their titles, messages, tool calls and results, instructions, indexes and saved sessions. Memory and private notes are encrypted too.

- Daily Memory and Reflection can read your encrypted conversations while the vault is unlocked. Conversation indexing and recall need an unlocked Vault Password, **Enable Memory** and a working embedding provider.
- Logging out leaves the key unlocked in the running Host. Restarting locks it. While locked, conversations and task execution wait for you to unlock, and scheduled attempts create no run records.
- Hooks receive identity and status fields without prompt text, attachments, tool arguments, task parameters, summaries, errors or decision reasons. Task outcome notifications use a generic message. Open the task to inspect its result.

### Use encrypted text files

Select the encrypted file tools the agent needs. They read, write, append, edit and patch files inside its allowed folders. Keep the original extension and append `.enc`, such as `notes.md.enc`.

- File paths stay visible. Contents are authenticated ciphertext, which is what ordinary file tools and shell commands see.
- The Files editor opens valid encrypted text after the vault is unlocked and encrypts it on save.
- Long tool output is saved encrypted too. Select the encrypted reader or search tool when the agent needs to inspect it. Encrypted write tools are separate selections.
- Duress credentials keep the vault and encrypted files unless your deletion selection removes them.

## Use Host Shell when Docker cannot persist a host tool

In Docker execution, normal commands run in Docker. An agent allowed to use **Host Shell** can ask for approval to run a foreground command as your current host user. Use it only for host-persistent installs or host-only tools, and keep normal project work on the ordinary shell tool.

- Host Shell appears under **Allowed Tools** only when the selected execution provider supports it. Docker does.
- Selecting it approves nothing. Its default policy asks before every call. Choosing **Auto Approve** removes that prompt under the saved agent policy.
- Scheduled tasks cannot use it. In delegated web-chat work it follows the called agent's selected tools and the parent chat's approval flow. Delegated surfaces without that approval route omit it, even with **Auto Approve** saved.

## Expose an agent as a tool

Turn on **Expose as Tool** so other agents can call this agent, then select it in the caller's **Agent Tools** list. Use this to delegate a focused job, such as release notes, triage or file cleanup, without putting every instruction into one agent.

- A delegated call uses the called agent's instructions, default model provider, file access, compaction and allowed tools. In web chat it uses the chat's approval flow. Where that flow is unavailable, approval-required tools are omitted.
- Within one chat, a call continues the called agent's default conversation unless the caller names one. Reusing a name continues that work, and a new name starts a separate child conversation. Delegated conversations appear as read-only tabs in chat.
- A delegated call does not attach that agent's memory, skills, private notes, Library context, to-do list or extra run providers. Run the agent directly when the work depends on them.

## Edit or delete

Select **Edit** on an agent card. Saved changes apply to future runs. A response already in progress keeps its starting configuration. Leaving the page with unsaved changes asks whether to discard the draft.

Delete removes the agent after confirmation, removes it from the chat selector, and cleans up its threads, scheduled-task references and indexed chat memory for every user, including when the agent is or was shared.

> **Caution:**
> Deleting a shared agent affects every user who can see it. Confirm no one depends on it first.

## Related articles

- [Shared agents](references/user-guide-chat-and-agents-shared-agents.md)
- [Agents, tools and skills](references/user-guide-concepts-agents-tools-skills.md)
- [Tool governance](references/user-guide-concepts-tool-governance.md)
- [Main agent settings](references/user-guide-chat-and-agents-main-agent-settings.md)
