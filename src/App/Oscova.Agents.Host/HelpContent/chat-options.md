# Chat options

Choose the agent, model provider and tool approval behavior for your conversation.

- **Agent** is selectable before the first message when additional agents are available. Existing conversations show their current agent.
- **Model Provider** shows your configured providers and their models. A single selected provider is shown as the current choice.
- An invited agent uses its own provider unless its **Use chat model when joining another agent's chat** setting is enabled. The composer shows the invited agent's provider and disables model selection when that setting is off. Removing or sending the recipient restores the owner's model choice.
- **Approvals** controls how new tool requests are handled. Use agent policy follows the agent's configured rules. Auto-approve allows requests that would ask for approval. Auto-reject rejects those requests.
- **Full access** is available only to the operator. Read its device-specific description before selecting it.
- **Session permissions** lists retained tool grants, including the agent and execution device when available. Revoke one grant or revoke all to require approval again.
- A running request keeps its original approval mode. The running-mode notice appears when it differs from your new selection.

> **Note:** choices apply immediately. Session grants may be unused while an approval override is selected, as indicated in the grant list.

## Goals

- Type `/goal` to enter an objective in the composer, or `/goal <objective>` to start directly. Starting a goal restores your ordinary draft and adds no chat message.
- The strip above the composer shows progress and elapsed working time. **Pause** stops pursuit. **Resume** continues a paused or blocked goal after you resolve its reason.
- **Edit** puts the objective in the composer. **Save goal** applies it, and **Cancel** discards the edit. Both restore your ordinary draft and attachments.
- **Clear** removes the goal and disables further continuation. A worker already running can finish its current work.
- **Stop** pauses goal pursuit. Provider, budget and iteration limits also pause it with a short reason. Fix the cause before resuming.
- If the agent posts questions, answer or dismiss them first, then click **Resume**. Answering alone does not resume the goal.
- Goal checks use the selected chat model and incur additional usage. Review the resulting work because model judgments can be wrong.

## Related

- [Table](dialog.chat-table.md)
- [Dashboard](dashboard.md) - conversations and workspace controls.
