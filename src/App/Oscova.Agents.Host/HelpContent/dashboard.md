# Dashboard

The dashboard is your chat workspace for conversations, tool activity, and agent-created files.

## Chat

- Use the persistent sidebar toggle below the app icon to hide or show the desktop conversation list. **Close sidebar** beside the heading also hides it. Settings navigation follows the same pattern.

- The navigation rail keeps **Chat** and **Tasks** together, followed by **Dashboard** for your usage and task activity.
- Operators also see **Administration**, followed by **Notifications** for everyone.
- Open **Settings** with the gear directly above your account button at the bottom of the rail.
- On desktop, **Toggle sidebar** hides or restores the projects and conversation list. On smaller screens, use **Show chats** in the chat header.

- Use **New Chat** for a General conversation or the plus beside a project, then choose the agent, model provider, and approval mode.
- The composer puts the message first, with agent, model, approval, attachment, speech, and send controls underneath.
- Unsent text stays with each chat while you switch chats or visit Tasks in the same browser connection. Sending or clearing it removes the draft. Reloading the page or closing the tab clears unsent drafts.
- The chat owner and Incognito mode are fixed after the first message, while the provider and approval mode can still change.
- To consult another custom agent here, type `@` at the start of the message and select a name. The removable token targets that message only. The next ordinary message returns to the chat owner.
- Consulted agents retain their work in this chat and receive the shared discussion and earlier chat uploads. Their answers and tool activity appear here under their names. Their configured access and approvals still apply.
- An agent may use its permitted agent tool with destination `chat` to consult someone you already invited. The default `delegated` destination keeps separate work in its existing tab.
- Shell approvals show the execution target and expanded arguments before you decide.
- **Allow once** approves one pending call, and **Deny** rejects only that call.
- **Allow for this session** remembers this tool for this conversation, agent and execution target.
- **Session permissions** lists current grants, with scrolling for long lists and **Revoke** or **Revoke all** for future calls.
- Refreshing or changing chats keeps grants, while restarting Oscova clears them.
- Saved approval labels are history and never restore permissions.
- The conversation saves **Use agent policy**, **Auto-approve**, **Auto-reject**, or operator-only **Full access**.
- **Full access** runs shell commands outside Docker on the selected project device or Oscova server.
- Hover or focus a preset to preview its meaning. Check the named machine before selecting **Full access**.
- Changes apply to subsequently admitted runs, and the running turn keeps its captured preset.

> **Note:** Full access reaches everything accessible to the process account, without adding administrator privileges. Disabled execution, Offline mode and denied tools still apply.
- **Auto-approve** can run host shell commands without a human prompt.
- Before sending, select **Incognito** for a temporary chat that is not saved or added to memory.
- Incognito chats and their attachments are deleted at sign-out, after inactivity, or when Oscova restarts.
- External models and tools may still receive incognito content.
- Conversations are grouped by recency, and search ranks matches from conversation titles and messages.
- Recent exchanges load first. Select **Load older messages** above the history to read earlier exchanges without moving your reading position. **Start of conversation** appears when all earlier history is loaded.
- Unsent drafts do not appear in the conversation list.
- Ask an agent with saved-chat listing selected to find an earlier conversation from words in its title or messages.
- It returns titles and short matching excerpts across your saved chats, including other agents and projects.
- Unlock your **Vault Password** first. Listing works without **Enable Memory** or an embedding provider and excludes incognito.
- On larger desktop windows, drag the dividers beside chat to resize the conversation list and Workspace. The browser remembers both widths.
- Enter sends, Shift+Enter adds a line, and Escape stops the selected running response.
- While the agent is working, Enter sends text using your saved **Follow-up behavior**. **Steer** joins the current response at its next model call. **Queue** waits and sends a separate turn after the current one finishes.
- Ctrl+Enter uses the other behavior for one message. **Stop** remains available beside Send while you type.
- Above the composer, use **Steer** to move a queued message into the current response, **Delete** to remove it, or **More actions > Edit message** to remove its row and return its text to the composer. The panel disappears when empty. Editing a message that has already started refreshes the list without restoring its text.
- Stop or failure pauses queued messages. Steering text the agent had not received returns to its original composer without replacing a newer draft.
- Follow-ups accept text only. Queued messages are lost when Oscova restarts, and pending composer recovery lasts while the current chat page remains open.
- After stopping an unfinished response, an empty composer shows **Resume**. Click it to continue with the saved partial reply and completed tool results. Resume adds no user bubble.
- Type text or attach files to send a new message instead.
- Saved chats keep **Resume** after restart and Vault Password unlock. Incognito chats remain temporary.

> **Note:** Resume asks the agent to continue without repeating earlier text. It starts a new response, so wording can vary and interrupted actions may need checking. Stop does not undo completed changes.

- Type `/` as the first character to choose `/compact`, `/context`, `/goal`, `/new`, `/rename`, or `/research`. Use Up/Down, Tab, Enter, Escape, or the pointer to operate the menu.
- Slash commands run as chat actions and are not sent to the agent, except `/research <topic>`, which asks the agent for a sourced report saved under `research/` in the default workspace. `/compact` summarizes older model context without changing the visible transcript; `/context` opens the current estimate.
- Pending attachments remain staged after `/compact`, `/context`, and `/rename`. `/new` clears them with the current draft.
- Different threads can run together, while **Stop** affects only the selected thread.
- Your latest conversation or **New Chat** selection stays active when an earlier conversation finishes loading.
- Subagents and called agents appear as read-only tabs as soon as they start, with their state, such as **Running** or **Completed**.
- Select one of these tabs to review the exchange and expand its tool calls to inspect arguments and results. Return to the parent tab to continue chatting.
- While the agent waits for subagents, the chat shows **Waiting for subagents**. You can still send messages, answer its questions or select **Stop**, which also stops its subagents.
- The caller can name separate conversations when it needs more than one line of work with the same agent.
- New replies follow the bottom until you scroll upward, and **Jump to latest** resumes following.
- **Branch in new chat** beside Copy opens an independent chat through that completed reply, with its saved context and files.
- Ask for an interactive explanation to see a visualization inside chat. New main agents include the required skill and tools.
- Follow-up edits update every appearance of the same visualization. Branches keep independent copies, and deleted files show **Visualization unavailable**.
- Visualizations keep their files encrypted. User or agent **Local only** disables them, and interactive control values reset when they reload.
- Branches use the original title with a number, such as **Project notes (2)**. Branching again uses the next family number.
- A muted **Continued from chat** row below the copied reply stays at the copied-history boundary and opens the source. If the source was deleted, **Source chat unavailable** replaces the link and the child remains usable.
- If a saved chat has an unsupported format, recovery guidance appears and your current chat stays open. Use a compatible application or restore a compatible backup. This also applies to notification and **Continued from chat** links.
- Branch is available on completed text replies with saved context, while the source chat is idle. Incognito branches remain temporary.
- **Regenerate** replaces the latest completed answer without adding the prompt again.
- **Retry** resends failed text without old attachments, so attach any needed files again.
- Question cards accept one suggested answer or your own text for each question.
- **Submit answers** continues the conversation, while **Cancel** closes the questions silently.
- New main agents include **User Question** for web chat. Enable it manually for existing or custom agents.
- Sending another message makes an unanswered card inactive. Reload preserves pending cards but loses unsaved answer drafts.

> **Note:** a red service status disables chat until configuration is fixed and **Retry now** succeeds.

## Review work

- Use the **+** beside **General** or a project to start a chat there. Section names expand or collapse their conversations without changing the open chat. The chat header shows its destination.
- **Files** previews the project and other allowed folders. General chats can browse the selected agent's folders before sending a message. Browsing another folder does not change the agent's working folder.
- **Changes** shows repository-wide staged, unstaged, and untracked changes when supported Git is installed.
- Select a changed file to read additions and removals in one column. New files appear under **Untracked** with their contents marked as additions.
- Refreshing changes keeps the selected file open while it remains in the same change group. Switching conversations clears the selection.
- Child agents inherit shared folders temporarily and use their own tool approval policies.

> **Note:** Project files remain ordinary files on their selected computer. Removing a project keeps its chats and files. Project history remains available while its node is offline, but execution waits for that node to reconnect.

- Expand a transcript activity line to see its tool calls, then select a call to inspect input, result, and timing. Scroll within an expanded group to browse long activity without losing your place in chat.
- After **Stop**, unfinished tool calls show **Cancelled**, including when you reopen a saved chat.
- If a tool rejects an unsupported named choice, its failure names the argument and allowed choices. The agent receives the same correction.
- **Technical details** reveals full arguments and tool identity. **Show full result** expands a shortened result.
- **Open workspace** shows files and artifacts in one document tab strip. Use **New tab** (+) for **Files**, **Changes**, a browser tab, or an artifact. **Enter full view** expands Workspace, and **Return to chat** restores the split view.
- Select a file link or local image in an assistant reply to open its Workspace tab. Local images display in chat when the file is available and permitted.
- Project artifacts open from their owning device while its node is connected. Start the node and reopen the preview if that device is unavailable. Text previews are limited to 512 KiB, and larger files can still be downloaded.
- **Files** works before your first message. Select a folder root above the list, drag its divider to resize it, and select a file to open a tab. Use **Edit** for supported writable text.
- Right-click a file or use its ellipsis menu for **Add to chat**, **Copy**, or **Download**. Add to chat puts a removable file card above your message. After sending, the filename card remains and your typed text stays intact. Download saves a copy on your current device, including when the original is on a node.
- **Refresh** picks up external file changes. Saving or finishing an agent run also refreshes previews.
- **Preview** shows unsaved Markdown, and **Save** appears only when you have changes.
- **Cancel** restores the last loaded or saved text without writing.
- Switching document tabs or hiding Workspace keeps drafts. Closing a dirty document or replacing its scope opens the unsaved-changes dialog.
- Editable text must fit within 512 KiB and 5,000 lines, including decrypted `.enc` text.
- Node files save on their connected device, and encrypted files stay encrypted.
> **Note:** A failed Save keeps your draft, and an interrupted reply may follow a completed write.
- Open a text or Markdown artifact and select **Copy** to copy its source.
- Encrypted `.enc` artifacts open as text after Vault Password unlock. Their downloads remain encrypted.

## Browse websites in Workspace

- With Browser enabled, use **New browser tab** or open an ordinary web link in chat. Enter a web address or search phrase. Back and Forward follow that tab's history.
- You and the agent share the page. Ask the agent to read or interact with it, or use your desktop mouse and keyboard directly. Phone viewing is available, but website interaction requires a desktop.
- Website uploads choose files from your device. **File ready** means a download has reached the Oscova host. **Save to device** transfers it to this device.
- Each user can have eight live tabs across chats. Close a tab to free a slot. Regular chats remember tabs after restart and reload them when opened. Incognito tabs and website logins are temporary.
- Website sign-in may require your help, and some sites reject automated browsers. Offline and Local-only restrictions disable agent browsing while manual browsing remains available.

## Connection recovery

- When the connection drops, Oscova makes up to three automatic reconnect attempts, waiting five seconds before each attempt.
- Choose **Retry** if automatic reconnection fails.
- If your session has ended, choose **Reload** to continue. Unsaved changes may be lost.

## Related

- [Chat options](chat-options.md) - agent, model provider and tool approvals.

- [Main Agent](settings.main-agent.md) - configure the default chat agent.
- [Agents](settings.agents.md) - create the custom agents shown here.
- [Add project](dialog.add-project.md) - help for this dialog.
- [Rename conversation](dialog.rename-conversation.md) - help for this dialog.
- [Context usage](dialog.context-usage.md) - help for this dialog.
- [Context window warning](dialog.context-window-warning.md) - help for this dialog.
- [Unsaved workspace changes](dialog.workspace-file-decision.md) - help for this dialog.
