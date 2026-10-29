# Main agent chat

The dashboard is where you chat with your AI agents. From one screen you start a conversation, pick its agent and model, attach files, approve tools, talk by voice, work inside a project folder and inspect the work behind every reply. Saved chats are encrypted on disk under your Vault Password, and Incognito chats stay in memory only.

This article follows the screen in the order you use it: start a chat, send messages, approve tools, then the deeper features such as goals, projects, the Workspace panel and website browsing.



## Start a chat

Select the plus button above the chat list. If you leave the new chat empty, returning to the dashboard can reopen your most recent saved conversation.

### Choose the agent and model provider

- **Agent**, beside the message box, chooses who owns the conversation. The agent is fixed after the first message, so start another chat to use a different agent.
- The provider selector chooses the model connection. A **model provider** is a saved connection to a local or hosted AI model. You can change it between turns, and the choice never edits the agent's saved default.
- The default chat provider initializes the selector. Changing the draft agent keeps the selected provider. An invited agent's model follows its own setting, described under Consult another agent.

Before the chat starts, Oscova checks the selected provider and shows progress:

- If no provider exists, add one.
- If providers exist but none is the default, choose one beside the message box.
- If the check fails, fix the provider or credential and select **Retry now**.

The service status indicator keeps checking the active provider. When it is red, the input row is disabled until you fix the provider, credential or local service and select **Retry now**.

### Use a starter card

An empty chat shows three starter cards picked from a larger set, refreshed each hour. Your first chat opens with a welcome and a tour card. Selecting a card fills the message box. Edit the prompt if needed, then select **Send**. A card never sends by itself.

### Make the chat Incognito

For a temporary conversation, select **Incognito** in the chat header before sending. The header shows a persistent notice and the chat carries an **Incognito** badge in the list. The setting is fixed after the first message.

Incognito chats:

- are not saved and are not added to memory;
- are deleted with their attachments when you sign out, after about 30 minutes of inactivity, or when Oscova restarts;
- do not stop external models or tools from receiving what you send them.

If the active Incognito chat expires, Oscova opens a new ordinary draft and shows a notice. You can also delete an Incognito chat from its row menu.

## Send a message

- Press Enter to send, Shift+Enter for a new line.
- Escape stops the selected run while a response is running. Enter does not cancel it.
- You can send supported attachments without typed text.

Unsent text stays with each chat while you switch conversations or visit Tasks in the same browser connection, including a new chat before its first message. Sending or clearing the text removes the draft. Drafts live in memory only: reloading the page, closing the tab or restarting Oscova loses them, and pending attachments are dropped when you leave the chat screen.

### Attach files

Attach files with the paperclip before sending. Attachments belong to that message and are separate from the long-term workspace files uploaded through the [Files](references/user-guide-knowledge-and-files-files.md) screen.

- Supported types: PNG, JPEG, GIF, WebP, TXT, Markdown, JSON, CSV, LOG, XML, YAML and PDF.
- Up to 50 attachments per message.
- Each attachment 10 MB or smaller, and the whole upload below 200 MB.
- Larger reference material belongs in the Files screen or an assigned workspace folder.

The agent can analyze image attachments only when the active provider has **Supports images** enabled. Without it, images stay attached but the agent may not be able to inspect them.

### Watch the context estimate

When the selected provider has a saved **Context Window Size**, Oscova estimates the draft, prior session context, provider instructions, tools, attachments and reserved output before sending. If the estimate is too large, you can cancel and shorten the message, or continue anyway. Pending attachments upload only when you choose to send.

The context ring in the chat header shows used and remaining space. Select it for the breakdown, or type `/context` to refresh it. It is a planning estimate, not the provider's billed token count.

## While the agent works

### Follow the response

An accepted response keeps running if you navigate away, switch chats, refresh, close the tab or end a CLI viewer. Return to the chat to see the current partial response and watch it update. New content follows the bottom while you stay near it. Scroll up and following pauses until you select **Jump to latest**. Several chats can generate at the same time.

If a run pauses for a tool approval or a question, returning to the chat shows that card so you can answer and continue the same run.

### Stop, resume and retry

- **Stop** cancels the active run in the selected chat, including while it waits for approval. Other chats keep working and your draft is kept. Shutting down the Oscova host also cancels in-flight work.
- Stopping keeps the partial response. Unfinished tool calls show **Cancelled**, completed tool results stay available, and changes a tool already made are not undone.
- After a stop, an empty composer shows a Play icon labelled **Resume**. It continues from the saved partial reply and completed tool results without adding a user bubble. The agent is asked not to repeat earlier text and to check interrupted actions before retrying them. The continuation appears as a separate assistant bubble, and its wording can differ because it is a new response.
- Typing a new message or attaching files uses **Send** instead of Resume.
- A saved stopped chat still offers Resume after a restart and Vault Password unlock. Restarting never resumes automatically, and a forced shutdown before saving cannot guarantee recovery.
- A failed text response shows **Retry**. It sends the failed text as a new turn without replacing what you have typed. Previous attachments are not uploaded again, so attach them again if needed. Attachment-only failures do not offer Retry.
- If starting or uploading fails, Oscova restores the draft and pending attachments when it can.

### Send follow-up messages

While the agent is working, Enter sends a follow-up using **Follow-up behavior** in **Settings > General**:

- **Queue** (the default) runs each message as a separate turn, in order, after the current response finishes.
- **Steer** joins the current response at its next model call.
- Ctrl+Enter uses the other behavior for one message.

Follow-up rows appear above the composer. Use **Steer** to move a queued message into the current response, **Delete** to remove it, or **More actions > Edit message** to return its text to the composer. Accepted steering text shows **Steering** until the agent receives it.

Limits and recovery:

- Follow-ups accept text only. Attachments and slash commands use normal turns.
- Stop or failure pauses queued messages until a later web turn completes. A turn from a messaging channel on the same chat does not drain the web queue.
- Queues are held in memory and are lost when Oscova restarts.
- A denied follow-up returns its text to the original chat's composer. Steering text the agent never received returns there once when the run ends, appended to any newer draft.
- Editing a queued message that has already started only refreshes the list.

### Answer agent questions

An agent can ask up to four questions at once. The card becomes active after the agent finishes its response.

1. Select each heading.
2. Choose a suggested answer or write your own. Nothing is selected automatically.
3. Select **Submit answers** to send all answers and continue.

**Cancel**, or Escape inside the card, closes the questions without continuing. Sending a normal message instead makes the unanswered card inactive. Pending questions survive a reload, unsaved answer drafts do not. Incognito questions disappear with their chat.

New main agents include the **User Question** tool. For an existing main agent or a custom agent, enable it under **Allowed Tools**. It works when that agent is selected directly in web chat and collects preferences and missing information. Tool permissions still use their own approval prompts.

If submission fails, your choices stay while the question is active. Select **Submit answers** again. If an interrupted response leaves an unfinished card, send a new message to ask again.

## After a response

- **Copy** beneath the final text bubble copies the original Markdown, including code indentation.
- **Regenerate** is available only on the latest completed response to a human request. It replaces that response without repeating the prompt and keeps no alternate versions. If the replacement fails, the chat shows the usual failed-run recovery.
- **Branch in new chat** opens an independent chat from that point. See Branch a chat.

Each table in a reply has **Copy** and **Expand table** above it. Copy produces tab-separated text with headings, ready for a spreadsheet. Expand table opens a larger read-only view that scrolls in both directions. Code blocks have their own **Copy**, which copies only the code and preserves spacing. If a copy fails, try again or select and copy the text manually.

## Approve tools

When a tool requires approval, Oscova shows the tool name and its arguments before continuing. Shell and skill-script approvals start with their full arguments expanded and say whether they run on the host or in Docker. Approving a skill script runs the whole script. Its commands are not approved one by one.

### Read tool activity

Consecutive tool calls collapse into one activity line. It names the first two tool categories, adds "and more" for others, and counts every call. Assistant commentary, reasoning and requests for approval or information start a new group. Failed tools appear in the warning color with their count.

Expand the line to see every call in order, then select a call for its input, result and timing. **Technical details** shows the full arguments, the exact tool name and the timestamp. Long results show 1,600 characters first, and **Show full result** reveals the complete retained output. Details you open stay open while activity arrives.

### Decide on an approval card

Each card offers:

- **Allow once** approves that pending call.
- **Allow for this session** approves it and remembers the tool for this conversation and executing agent, across different arguments. Shell and skill-script grants also include the selected project, device and Host or Docker target.
- **Deny** rejects the call without creating a permanent deny rule.

If Oscova cannot establish the tool's identity, session approval is disabled and you can still allow once or deny.

### Choose an approval preset

**Approvals** beside the composer saves one preset for this conversation. Hover or focus a choice to preview its meaning.

- **Use agent policy** follows each tool's policy. Matching session permissions satisfy approval requests.
- **Auto-approve** approves otherwise available tools that ask for approval.
- **Auto-reject** rejects approval-required calls, including calls with a matching session permission.
- **Full access** is operator-only. It approves otherwise available tools and runs the ordinary shell outside Docker on the named execution machine, with the destructive-command filter disabled.

> **Warning:**
> Before selecting Full access, check the machine named in the menu. A project on a connected device runs there, and a disconnected device fails rather than falling back to the server. Commands get the reach of the operating-system account running that process, without added administrator privileges. Tool denial, account permissions, Offline or Local-only restrictions, disabled shell execution and operating-system limits still apply.

The preset applies to runs admitted after you change it. A running turn and existing background jobs keep their captured preset and target, and the composer names the running turn's actual mode when it differs. Use **Stop** to interrupt current work.

Auto-approved calls show a compact **Auto-approved** status. Expand the activity and its approval row to see the saved tool, agent and decision, such as **Auto-approved by session permission** or **Full access**, plus the machine and target when available.

### Manage session permissions

**Session permissions** beside the composer lists the current grants from the server. **Revoke** removes one, **Revoke all** removes every grant for the conversation. Revoking affects future decisions only: approved work is not undone and running processes are not stopped. Changing presets keeps grants, Auto-reject takes precedence over them, and revoking grants does not turn Full access off.

### What survives a refresh or restart

| State | Refresh or change chats | Restart Oscova |
|---|---|---|
| Allow once or Deny | The decision stays in history. A later call may prompt again. | History remains without granting permission. |
| Allow for this session | The grant remains for this conversation and executing agent. | The grant expires. Its historical label remains. |
| Saved composer preset | The choice remains. | The choice remains, with current authority checked before execution. |
| Incognito permissions and history | Remain while that temporary conversation exists. | Discarded. |

A new chat or branch starts with Use agent policy and no grants. Copied history carries no authority. A pending request that is stale after a restart shows that it expired, and the agent must request the tool again.

## Use slash commands

Type `/` as the first character to open the command menu. Keep typing to filter, use Up and Down to move, Tab to complete, Enter to run and Escape to close. Commands run locally: they are not added to the transcript or sent to the agent, except `/research`.

| Command | What it does |
|---|---|
| `/compact` | Asks the selected model to summarize older session context while protecting the two most recent user turns. Available once the chat has a saved model session. The visible transcript does not change and pending attachments stay staged. |
| `/context` | Refreshes and opens the context-usage estimate. Available when a model provider is selected, including on a new draft. |
| `/goal` | Opens goal entry. `/goal <objective>` saves the objective and starts goal pursuit without adding a human message. |
| `/new` | Starts a new draft through the normal new-chat action, clearing pending attachments with the old draft. |
| `/rename` | Opens the rename dialog. A new draft is renamed locally, a started chat uses the saved-conversation rename. Pending attachments stay staged. |
| `/research` | Completes to `/research ` so you can add a topic. `/research <topic>` is sent to the agent as an ordinary message, shown as typed, and answered with a sourced Markdown report saved under `research/` in the default workspace and linked in the reply. During a reply it is sent as a follow-up. |

An unknown or unavailable command stays in the message box with an error instead of being sent.

## Work toward a goal

A goal keeps the agent working on an objective across several replies until a completion check finds nothing left to do.

1. Type `/goal`, enter the objective and select **Start goal**. Or type `/goal <objective>` to start directly.
2. Watch the goal strip above the composer. It shows the objective, state and cumulative working time.
3. Review the result yourself. A model can wrongly judge a goal complete, so ask for evidence such as the finished file's contents.

How it runs:

- The objective is saved separately from chat messages. Starting while the agent is busy lets its current work finish first.
- After each reply, a completion check assesses the objective against the latest response. Checks use the same model and provider and add token usage.
- Steer and Queue behave as usual. Queued messages run before more goal work. Goal tools appear automatically during pursuit.

Controls on the strip:

- **Pause** stops pursuit and keeps progress. **Resume** continues a paused or blocked goal with a fresh iteration allowance and the same session.
- **Edit** borrows the composer. **Save goal** applies the change, **Cancel** discards it, and both restore your ordinary draft and attachments. The goal's state does not change.
- **Clear** removes the goal and stops further continuation. A worker already running finishes its current work.
- The ordinary **Stop** button pauses the goal with **Stopped** until you select **Resume**.

A paused or blocked strip shows a short reason. Resolve missing input, provider access or a budget limit before resuming. If the agent posted a question card, answer or dismiss it, then select **Resume**. Answering is an ordinary chat turn and does not resume the goal by itself.

Settings:

- **Goal max iterations** in the agent editor is your personal cap. A blank **Default** allows completion-driven work up to 2,147,483,647 cycles, with budget limits still enforced.
- **Goal judge instruction** and **Goal continuation template** live under **Settings > Instructions**.

Saved goals and instructions are encrypted under your Vault Password. After a restart, an active saved goal is paused with **App restarted** until you select Resume. Incognito goals disappear with their chat. Working time excludes pauses and downtime.

## Work in a project

A project ties a chat to a folder on a connected computer, so file tools, shell commands and Git inspection run there.

### Add a project

1. Select **Add project**.
2. Choose a connected device. The operator can also choose the server. To connect a laptop, follow [Device nodes](references/user-guide-getting-started-device-nodes.md).
3. Enter a name of 1 to 100 characters and the absolute path of an existing folder on that device. Oscova does not create the folder.
4. Select the **+** beside the project to start a chat there, and choose an agent with coding tools. The **+** beside **General** starts a chat without a project.

Requirements:

- Regular users need the Projects permission from the operator, and the operator's project-shell permission to run commands.
- Install Git on the device to use **Changes**, and install the tools your repository needs in the selected execution environment.

The chat header shows **General** or the project and folder. Relative file and shell paths resolve against that folder. The agent keeps its other configured folders and private data. Delegated agents receive the shared folders temporarily and keep their own tools, privacy restrictions and approval requirements. Approval cards name the requesting agent.

### Where commands run

Project commands use the operator's selected [execution environment](references/user-guide-settings-and-admin-execution.md) on the project's computer:

- Host commands run with that process's operating-system access.
- Docker and Microsoft MXC need their plugins and prerequisites on that device. An unavailable environment never falls back to Host.
- Local-only restrictions still block Host shell execution. File tools stay available when shell execution is disabled.
- Server workspace files remain reachable through explicit workspace requests in a project chat. Attachments uploaded to the server stay there: the agent reads them read-only through the file tool's workspace environment, and they are never copied to the device.
- An offline project keeps its chat history, but new project actions wait until the same node reconnects. Requests never fall back to the server or replay interrupted writes.

### Review files and repository changes

Open **Files** in Workspace to browse the project or another allowed folder. Selecting a folder changes the preview only.

**Changes** shows the whole repository, including edits made outside Oscova:

- Staged changes compare the last commit with the index, unstaged changes compare the index with working files, and untracked files compare against empty text.
- Select a changed file to read additions (`+`) and removals (`-`) in one column. New files appear under **Untracked**.
- Select **Refresh** to pick up external edits. Saving a file or finishing the parent run also refreshes the panels.
- Repository comparisons are read-only. Child transcript tabs keep the parent's project scope.

Limits:

- Listings show up to 500 direct entries.
- Text and each comparison side are limited to 512 KiB and 5,000 lines.
- Change lists show up to 2,000 entries within 2 MiB of status output.
- Binary, oversized, conflicted and unsupported files show a status instead of a comparison.
- Supported: a repository whose `.git` directory sits inside the selected root. Unsupported for review: linked worktrees, submodules, external metadata, included Git configuration and files using filters such as Git LFS. An unused global LFS registration is fine, and files and coding still work.
- Missing Git or local objects produce an unavailable state. Nothing is downloaded.

### How projects are stored

- Project settings persist independently of chats, and a saved chat keeps its project association.
- Removing a project from its section menu keeps its files and chats, grouped as unavailable. Adding the same path later creates a new project without reassigning those chats.
- If a folder disappears or access changes, restore access or start a chat in an available project.
- Project chats are encrypted and need your unlocked Vault Password. Project names, paths, repository contents and Git data stay ordinary files. In Incognito, the chat stays in memory while the project catalog and file edits persist.

## Use the Workspace panel

Select **Open workspace** in the chat header. Files, artifacts and browser tabs share one tab strip above the viewer. Select **New tab** (+) for **Files**, **Changes**, **New browser tab** or an item under **Artifacts**. Opening or focusing a browser tab can reveal the panel automatically.



Layout:

- The standard panel shares the window with chat. **Enter full view** gives the viewer the conversation area, and **Return to chat** restores the split. On smaller screens the panel overlays the conversation.
- Wide previews show file navigation on the right. Drag the divider, or focus it and use the arrow keys. Narrow previews use **Back** to return to the file list.
- On large desktop windows, drag the dividers beside chat to resize the conversation list and the panel. The browser remembers both widths on this device.
- The sidebar toggle below the app icon shows **Close sidebar** or **Show chats**. Settings navigation follows the same pattern with **Show settings navigation**.

### Browse and open files

You can browse **Files** before sending a message. A General chat shows the selected agent's allowed folders, and a project chat starts at its project folder. Choose a root at the top of the list.

- Selecting a file opens a document tab, and selecting it again reuses that tab.
- Close a tab with **Close**. Closing the active tab selects another open document, or returns to folder browsing when none remain.
- Switching to Changes, refreshing or hiding the panel keeps open tabs and unsaved drafts. Arrow keys, Home and End move between document tabs.

Right-click a file or select its ellipsis button for the file menu:

- **Add to chat** places a removable card above your unsent message showing the filename, workspace and path. After sending, the card stays beside your message and the file reference reaches the agent separately. The agent's existing access permissions still apply.
- **Copy** offers the relative path, the absolute path on the owning device, or the filename.
- **Download** saves the original bytes to the device running your browser, including files stored on another node. Editing that copy does not change the original.

### Edit a workspace file

1. Open a writable text file through **Files** or **Artifacts** and select **Edit**. Markdown opens in rendered preview, and **Preview** renders unsaved changes while editing. Other text opens in the source editor.
2. **Save** appears once your draft differs from the loaded text. Typing never saves.
3. **Cancel** restores the loaded text, exits editing and keeps the file open.

Rules:

- Edits replace the file on its original computer. Node files need that node connected and authorized.
- Encrypted `.enc` text must decrypt with your unlocked vault and stays encrypted after saving.
- Text must be valid UTF-8 without NUL characters, within 512 KiB (524,288 bytes) and 5,000 lines, including decrypted text. The original byte-order mark and LF or CRLF line endings are preserved.
- Drafts exist only in the open application. Save before closing or reloading. Your browser warns about unsaved changes.

Closing a dirty tab, switching or creating a chat, selecting another agent, changing roots, leaving the page or deleting the selected chat asks **Save**, **Discard** or **Cancel**. Every dirty file must save before the action continues. Discard continues without writing, and Cancel keeps the current scope.

If another writer changed the file, Save opens a conflict dialog. **Overwrite with my edits** rechecks and replaces the latest version. **Reload latest** asks before discarding your edits, fetches the current file and cancels any pending close or navigation. **Cancel** and failed reloads keep your draft. If a node disconnects or access changes, restore it and retry Save. A retry after an interrupted save can open the same conflict dialog.

### Inspect artifacts

**Artifacts** lists files the agent created, changed or linked in its replies, deduplicated by path and showing the current file rather than a saved copy. A file link in a reply opens its document tab. Local image references display in the reply, and selecting one opens its preview. The conversation's file permissions still apply.

Preview support:

- Markdown uses the same safe renderer as chat.
- Plain text, source code, JSON, YAML, CSV, XML and similar text use the source viewer and offer editing when writable.
- Encrypted `.enc` text needs your unlocked vault for preview and editing. Downloads stay encrypted.
- PNG, JPEG, GIF and WebP use the browser's image viewer. PDFs use the browser's PDF viewer.
- SVG and other formats show file details and a download action only.
- Markdown and text previews are limited to 512 KiB (524,288 bytes) and 5,000 lines. Larger files remain downloadable.

The preview header offers **Copy** for the original source and **Download artifact**, with the update time in the path's tooltip. Creating or changing a file does not force Workspace open. Closing the panel or a tab does not remove files. If a file is moved, deleted or no longer permitted by the agent's workspace access, the preview stops serving it.

## Browse a website together

With the **Browser** plugin enabled, select **New browser tab** in Workspace, enter a URL or search phrase and press Enter. Ordinary HTTP and HTTPS links in chat open here too. Modified clicks and your browser's link context menu keep their usual external behavior. If Browser is unavailable, chat links open externally.

The agent can list the chat's tabs, read a page, navigate the same tab, click, fill fields, choose options, scroll and transfer files. Ask it to use an existing tab to continue together. You can also interact directly without an agent turn. Simultaneous input can interfere, so coordinate through chat. The violet pointer marks agent actions and fades after three seconds.

Limits:

- Each user can have eight live tabs across regular and Incognito chats. Close one before opening another at capacity.
- Website uploads accept up to eight files totalling 32 MiB. Agent file transfers follow the agent's folder permissions and the same 32 MiB limit.
- Phones can view the page. Interaction and sign-in need a desktop.
- Back and Forward use the selected tab's history. Popups open beside their opener and return there when closed.

Files:

- Website file inputs open your device's file chooser when your browser permits it. Otherwise use the file-selection overlay.
- A **File ready** download has reached the Oscova host. Select **Save to device** to transfer it to the device you are using.

What persists:

- Regular chats remember tab titles, URLs and selection after a restart. Selecting a saved tab reloads its URL. Unsaved forms, scroll position and navigation history are not restored.
- Regular chats share your managed website profile. Cookies, site storage and site settings persist so website sign-ins survive. They are not encrypted with your Vault Password, and equivalent operating-system credential protection is not guaranteed.
- Browsing history, tab sessions and caches are deleted when the managed browser starts and stops. It never uses your personal desktop browser profile.
- Each Incognito chat starts with separate temporary website state, discarded with that chat or when the browser stops.

Where it runs: the managed browser runs on the Oscova host, so localhost addresses refer to that host. Chromium downloads on first use and needs internet access, disk space and its operating-system dependencies. The page is streamed as images, so website screen-reader access, IME composition and native operating-system authentication dialogs are not supported.

Sign-in and safety:

- When sign-in or verification blocks progress, the agent should ask for help and end its turn. Complete the step, then send a message to continue.
- Google sign-in is unsupported in the tested configuration. Other websites and identity providers can also reject an automated browser.
- Offline and Local-only restrictions prevent agent browser use, including localhost pages. Manual browsing stays available.
- Browser actions run with the website account's authority. Confirmation before consequential actions is agent guidance, so review what you authorize.

## Bring other agents into the chat

### Consult another agent in this chat

1. In an existing chat, type `@` at the start of the composer and choose a custom agent you can access. Use the arrow keys with Enter or Tab, or select the name.
2. The composer shows a removable recipient token. Add your question and send it.
3. That agent answers this message. Your next unaddressed message goes back to the chat owner.

For example, ask Aaron to implement a page, then address Barry for design advice. Barry's answer and expandable tool activity appear under his name in the same transcript. Address Barry again later and he keeps his earlier work and receives the discussion added since his last turn. Reopening a saved chat keeps this context. Incognito participant work disappears with the chat.

How consultation works:

- The invited agent sees the shared chat history and earlier uploads in that chat.
- It uses its own configured model, tools, instructions, skills and context features. Turn on **Use chat model when joining another agent's chat** in that agent's editor to make mentions follow the chat's selected provider. When it is off, the composer shows the invited agent's provider read-only. Sending or removing the recipient restores the owner's selection.
- The chat's privacy restrictions and approval rules still apply. The invited agent does not inherit the owner's private folder.
- If the active response belongs to another agent, your addressed follow-up waits in the queue.
- An agent can consult a participant you invited through its permitted agent tool, answering either in this chat or in a separate delegated conversation. A caller can also ask for an answer that ignores the earlier discussion, which uses a fresh session while the shared session is preserved. Text such as `@Barry` in an agent's answer never invokes anyone, and joining your chat grants no agent permission to call the participant.

### Review delegated conversations and subagents

When the chat owner starts a subagent or delegates work to another agent, a read-only tab appears as soon as the work starts. It shows a state such as **Running**, **Awaiting approval**, **Failed**, **Interrupted** or **Completed**, and refreshes about every two seconds while active. Select it to read the exchange and tool activity in order, expand a tool call for its arguments and result, then return to the parent tab to send another message.

- The calling agent can name a conversation. Reusing the name continues that work, and another name starts a separate conversation with its own tab. Calls made by the child stay nested in its tab.
- A subagent is a separate instance of the chat owner with a fresh history. Up to four run at once. They cannot start further subagents or ask you questions.
- When the owner has nothing else to do, the chat shows **Waiting for subagents**. You can still send messages and answer the owner's questions, also while a subagent result has the owner working again. The answer reaches the owner in that same run. Turn off **Use subagents** in the agent's settings to stop it from starting them.
- Child tabs accept no messages, attachments, approvals, voice input or run-option changes. While the parent run is active, **Stop** cancels it and its running subagents. Failed delegated calls add no child turns.

## Explore interactive visualizations

Ask something like "Can you help me see how a pendulum changes when I vary its length and mass?" The main agent includes the `visualize` skill and its companion tools by default and can build a self-contained interactive explanation inside its reply. Other agents need the skill and the Visualization toolset selected in their settings. The main agent also includes the `oscova-research` skill, which tells it how to answer `/research`: search and read sources, or ask a research agent it may call for cited findings, then write and link the report.

- Operate the sliders and buttons directly. Those interactions stay inside the visualization and send no chat messages.
- Ask for a change in a follow-up message to have the agent edit the saved document. Earlier appearances of the document show its latest contents.
- Switching chats or reopening a conversation restores saved positions. Controls initialize from the saved HTML, not your last slider values.
- Documents are encrypted with your vault and belong to the conversation. Branching copies them so edits in the new chat are independent. Incognito documents disappear with the chat.
- Ask the agent to delete a visualization using its file permissions. Its saved appearances then show **Visualization unavailable**, as does a missing or unreadable file.
- Visualizations cannot load external resources or call network APIs. They are disabled whenever your global or selected agent's Local-only setting applies, because a scriptable browser frame is not a complete network isolation boundary. Turning Local-only on removes active visualizations and keeps their encrypted files.

## Use voice

Voice controls sit near the message box:

- Microphone dictates one message.
- Speaker reads replies aloud.
- Voice mode starts a hands-free listen-and-speak loop.

All three need speech readiness. If they are disabled, check Speech settings for model downloads or provider configuration.

Enable the **Speech** tool for your agent to let it speak selected words, such as a brief takeaway beside a detailed written answer. It uses your saved Speech provider, voice and output destination even with **Read aloud** off, and leaves the button unchanged. With **Read aloud** on, the written reply can also be spoken. Earlier speech results do not replay when you reopen the chat. On a phone, tap the page if the browser blocks queued audio. Setup is in [Voice](references/user-guide-getting-started-voice.md).

## Branch a chat

Select **Branch in new chat** beside Copy below a completed text reply. Oscova opens an independent chat containing the conversation up to that reply. Branch is available when the source chat is idle and that reply still has retained context.

- The child keeps the model context saved at that point, including tool history and summaries. Later messages and later compaction in the source are excluded.
- The title adds a number, such as **Project notes (2)**. Branches of a child keep the family title and take the next number.
- The child keeps the source agent, project and Incognito mode. The provider is your current composer choice and can be changed.
- Conversation-owned attachments and retained tool outputs are copied. Project files, credentials and external services use their current state.
- **Continued from chat** marks the boundary after you send new messages or reload. Select it to open the source chat. Deleting the source leaves the child usable and changes the marker to the disabled **Source chat unavailable**.

## Find and manage saved chats

### Browse the chat list

Chats are grouped under **Today**, **Yesterday**, **Previous 7 days** and **Older** in your local time. Search checks titles and visible messages and shows the closest matches first without date grouping. Matching is case-insensitive and token-based, so partial words and typos may not match. On mobile, the list opens as a drawer.

Selecting a chat highlights it and loads its recent exchanges first. Returning to a loaded chat restores your reading position during the same browser connection. At the top of the history, select **Load older messages** for an earlier batch. Older exchanges appear above your view without moving it, and **Start of conversation** replaces the button when no earlier history exists. If loading fails, select the button again. Loading history does not shorten the agent's context or change its to-do list.

Hover a chat and use its row menu to rename or delete it. Rename accepts 1 to 120 characters whether automatic titles are on or off.

> **Caution:**
> Deleting a chat removes its saved conversation, staged attachments, artifact list and indexed chat-memory chunks. Files already written to a workspace remain. Export or copy important content first.

If a saved chat has an unsupported format, Oscova shows recovery guidance and keeps your current chat open. Use a compatible application or restore a compatible backup. The same applies when opening chats from notifications or **Continued from chat**.

### Ask the agent to find a chat

Ask an agent that has `chathistory__list` selected, "What is the title of the chat where we talked about Fall Guys?" It searches words in your saved chat titles and visible messages across agents and projects while your Vault Password is unlocked, and returns titles, thread IDs, dates and matching excerpts. Incognito and other users' chats are excluded.

- New main agents include the tool. For an existing or custom agent, select it under **Allowed Tools** and save. It is not pinned by default, and with Tool discovery on the agent finds it when needed.
- Listing needs neither Enable Memory nor an embedding provider.
- Results default to 10 chats per page, with a maximum of 50. Each result includes at most one excerpt of up to 400 characters with that message's timestamp in your time zone. The chat's last modification date is separate and changes when you rename it.
- Ask a follow-up such as "What did we decide in that chat?" and an agent with `chathistory__read` reads that chat's messages in numbered pages of up to 50, each cut at 2,000 characters. Tool calls appear by name only, without their arguments or results.
- Keyword matches can contain only some of the query words, so read the excerpts before choosing. The agent can fetch another page or look up a thread ID's current title. Listing leaves your current chat and unread indicators unchanged.
- Listing finds chats, it does not return the full transcript. Reading indexed passages by meaning uses the memory tools, which depend on memory availability and index coverage. See [Chat memory](references/user-guide-chat-and-agents-chat-memory.md).

## Related articles

- [First-time setup](references/user-guide-getting-started-first-time-setup.md)
- [Main agent settings](references/user-guide-chat-and-agents-main-agent-settings.md)
- [Voice](references/user-guide-getting-started-voice.md)
- [Chat memory](references/user-guide-chat-and-agents-chat-memory.md)
- [Files](references/user-guide-knowledge-and-files-files.md)
- [Device nodes](references/user-guide-getting-started-device-nodes.md)
