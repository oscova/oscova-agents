# Instructions

Edit shared instructions for chat resumption, titles, tool recovery, and how agents receive context.

> **In plain terms:** these saved templates guide background behavior across your agents.

## Edit an instruction

- **Edit** opens the selected instruction in its own editor.
- Choose **Use Default**, **Add to Default**, or **Replace Default** in the editor. Application updates refresh defaults while preserving your custom text and mode.
- **Restore default** asks for confirmation before clearing your custom text and switching to Use Default. Click **Save** to apply it. **Cancel** discards the restored draft.
> **Note:** saved changes apply to the next message or task run. Empty replacement text stays empty.

## Choose the instruction

- **Chat Resume Instruction** guides continuation after an interrupted reply.
- **Goal judge instruction** tells the selected chat model how to check completion. Preserve the `answered` and `gapAnalysis` response fields and the `VERDICT: DONE` / `VERDICT: MORE` text fallback.
- **Goal continuation template** becomes the worker's next instruction after an incomplete verdict. `{gap_analysis}` inserts the judge's explanation. It uses single braces.
- **Chat Title Instruction** creates automatic titles and supports `{{ chat_history }}`.
- **Tool Failure Recovery Instruction** guides recovery after repeated failures.
- **Browser use** guides website interaction, login assistance and confirmation before consequential actions. It applies to agents with browser access and guides model behavior rather than guaranteeing detection.
- **Environment Prompt** describes workspace access, the agent's own private folder and shell paths.
- `private_directories` lists the agent's private folder. `shell_root_variables` lists shell variable names beside folder aliases.
- `has_private_directory` and `has_shell_roots` control whether those sections appear.
- `workspace_has_aliases` and `uses_host_paths` are true or false values for environment conditions.
- Other rows cover compaction, memory, Library Search, tool discovery, task lists, skills, and avatar emotion tags.
- **Skills Instruction** requires `{skills}` for its generated skill list.

Your saved instructions are encrypted under your Vault Password.

## Related

- [Main Agent](settings.main-agent.md) - edit one agent's system instruction.
- [Chat Memory Store](chat-memory.md) - inspect saved conversation memory.
- [Edit instructions](dialog.prompt-editor.md) - help for this dialog.
