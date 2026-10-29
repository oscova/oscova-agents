# Agent tasks

An agent task is a saved job for an AI agent: an instruction, the agent that runs it, an optional model provider override, a trigger and its run history. Tasks run on demand, on a schedule, after another task or through an authenticated webhook, all on the hardware that hosts Oscova. Run transcripts, sessions, parameters, summaries and errors are encrypted under your Vault Password.



## Create a task

1. Open **Agent Tasks** and select **New Task**.
2. Choose the agent. Optionally choose a model provider. **Use agent default** uses the agent's **Default Model Provider**, or your app-wide default when the agent has none.
3. Write an instruction specific enough to run without follow-up questions: the input source, the expected output, where to save or send the result, what to do when data is missing, and whether to notify someone.
4. Choose a trigger and approval behavior, then save.

The task uses the agent's tools, skills, memory setting, workspace access and system instruction. Review that agent before scheduling work with side effects.

### Run scope

**Run Scope** decides whether the task inherits the agent's settings or carries its own:

- **Use agent settings** keeps the task aligned with the agent.
- **Custom task settings** stores task-owned tools, approval overrides, tool discovery, compaction, memory, task-list, subagent, local-only, agent-tool, skill and workspace settings. Use it when a daily job needs narrower access than the agent usually has.

A custom scope is saved on the task and applies to its future runs. It never edits the agent definition.

### Provisioned instructions

Tasks provisioned by Oscova or a plugin offer **Use Default**, **Add to Default** and **Replace Default** in the instruction editor, so the supplied instruction can follow updates while your custom text is kept. **Restore default** clears custom text after confirmation. Save the instruction editor, then save the task form. Cancelling the form keeps the saved instruction. Tasks you create use plain editing. See [Instructions](references/user-guide-knowledge-and-files-instructions.md).

## Manage tasks from chat

Ask an agent to list your tasks, inspect one with its recent runs, create, change, run or delete a task. It can also answer questions such as "Did yesterday's report task write the file?" by searching task names, instructions and run results, then reading the matching run's transcript. Before creating one, the agent should confirm the schedule and ask whether you want an extra inbox notice after success.

A proposed create, update, delete or immediate run appears as an approval card naming the task and owning agent. Create and update cards also show the schedule and how approval-required tools behave during unattended runs.

> **Important:**
> **Auto-approved during runs** means the task may use tools that normally need your approval for each call. Host shell commands can change the machine running Oscova. Approve this only when you trust the instruction, agent, tools, credentials and file access.

Chat-created tasks belong to the agent you are talking to, support interval, one-shot and cron schedules, and show an **Agent-created** badge whose tooltip names the creating agent. Use the task editor to choose another agent, configure a chain or webhook, or change the run scope.

## Choose a trigger

- **Interval** repeats every chosen number of minutes.
- **One-shot** runs once at a date and time.
- **Cron** runs on a recurring pattern, such as every weekday morning, in the task's saved time zone, taken initially from your user settings. The dialog helps build the pattern. Review advanced expressions yourself.
- **After another task** runs when the selected upstream task completes successfully.
- **Allow external webhook trigger** lets an outside system start the task by URL.

Interval, cron and one-shot schedules wait until your onboarding is complete. Due tasks start on the next scheduler check, within about 30 seconds while Oscova runs, and keep running while you are signed out.

### Wait for your provider

**Wait while I'm using the provider** starts on. A due task waits while one of your active requests uses its provider and starts on the next check, normally within 30 seconds after that request finishes. Waiting creates no run record and does not reset the schedule.

- Only your own requests on the same provider entry delay the task. Other users sharing an operator provider do not. A memory search can briefly use the embedding provider and defer Vector Indexing, while chat on a separate provider does not, even on the same server.
- **Run now**, webhooks and chained tasks always bypass the wait. A running task continues when you start chatting.
- Turn the option off to allow starts during your requests. The provider can still queue requests by its own capacity.

### Built-in tasks

New installations schedule six tasks with independent, editable settings:

- **Daily Memory** runs every 60 minutes and saves noteworthy parts of recent conversations to today's encrypted daily memory.
- **Vector Indexing** runs every 60 minutes and indexes new conversation messages so agents can recall past conversations. It needs an embedding provider in **Settings > Agents & AI > Model Providers**.
- **Dreaming** runs at 03:00 in your time zone and moves stable facts from recent memory into long-term memory. When today's and yesterday's daily memory are empty, it completes without calling the model.
- **Reflection** runs at 03:00 in your time zone and updates each agent's private lessons from recent behavior.
- **Night Shift** runs at 01:00 in your time zone and works up to three of the oldest to-do items you delegated to the agent, for example with "you do this tonight". It marks each finished item done with a line of evidence and leaves the rest open. Tools that need approval are rejected during the run. With no delegated item, it completes without calling the model.
- **Morning Brief** runs at 07:00 in your time zone and delivers one conversation named **Morning brief** with the date, plus a bell notice. It reports what Night Shift finished, whether the night failed or was skipped, the items that need you, the open items and a few lines from yesterday's daily memory. It removes done to-do items older than 30 days first. When nothing happened since the last brief, it completes without calling the model.

Daily Memory, Dreaming, Reflection, Night Shift and Morning Brief need a chat model provider. Without its provider, a task records a skipped run and its inbox notice says where to set it up.

## Use webhooks

A webhook is a URL another program calls to start the task.

1. An administrator creates a task webhook API key.
2. Enable **Allow external webhook trigger** and copy the full endpoint from the task editor. The address includes your user ID before the webhook name, so another user with the same name gets a different address. The slug follows the 80-character URL normalization, and API-key allowed slugs must contain the full owner-qualified slug.
3. Add a webhook parameter for each JSON field the instruction expects, such as `customerId`, `ticketId` or `summaryText`. Mark it required when the task must not start without it.
4. The caller sends `Authorization: Bearer <token>` and a JSON body with a `parameters` object.

Oscova validates required parameters before starting and returns a bad request for missing or wrong-shaped ones. An accepted run returns a run ID, status, message and status URL. If the task is already running, the caller receives a conflict response. External status callers receive status, trigger source and timestamps without summary or error content.

Webhook-enabled one-shot tasks cannot use **Delete task after run**, so the status URL stays available after the run.

## Choose approval behavior

Background tasks cannot show the chat approval prompt, so approval-required tools are handled automatically:

- **Auto-reject approval-required tools** is safer for read-only routines and monitoring.
- **Auto-approve approval-required tools** is for tasks whose instruction, agent, tools, credentials and file access you have reviewed.

> **Important:**
> Auto-approve removes the interactive approval step. Host shell commands can change the machine running Oscova, and broad tool access is not made safe by itself.

## Operate tasks

The task directory shows status, agent, schedule, last run, enabled state and actions.

- Use the row toggle to pause or resume a task. Pausing prevents new runs without cancelling one in progress.
- **Run now** starts a task immediately. It is disabled while the task is paused or running.
- Enabled cron tasks show their next run time above the pattern. Search matches names, agents and instruction text. **Show instructions** adds a short preview to each row.
- Select a row for details and recent executions, **View runs** for a larger list and **Inspect** when a run has stored messages.
- Deleting an active task stops its current run, then removes the task and its history.

### Notifications and agent messages

- **Notify on completion** is on for new tasks and sends an inbox notice after each successful run. Turn it off when success needs no notice. Tasks saved earlier keep their setting. Failed and skipped runs always send one, except a Vector Indexing run skipped for a missing embedding provider. Cancelled runs do not. Notices contain stock status text and a link to the task. A skipped-run notice names the reason, such as the missing provider and where to set it up.
- An agent can also message you during a run. Ask for it in the instructions and name the channel, for example "message me on Telegram when the report is ready". Without a channel the message goes to the web app. Discord, Telegram and Slack work when the channel is running. An agent message and an automatic notice can both arrive.
- Every task message creates a conversation named **Handoff from** and the agent's name, unless the agent gives the message a title, as Morning Brief does. Reply in the web app or on the notified channel to continue with that agent, even when the channel normally uses another agent.

You can also ask an agent in an ordinary chat to message you on Discord, Telegram or Slack when it finishes. Your channel reply continues that same chat with its agent and project. No copy of the chat is created.

The latest notification selects the conversation for that channel. Replies continue it for your **Resume recent chats** window, counted from the notification, without extending the window when you reply. A zero window disables this selection, and restarting Oscova clears it. After the window ends, the channel resumes its normal selected-agent behavior. Send `/new` to end the selection immediately and start your next message in a fresh conversation with the channel's selected agent.

### Skipped and failed runs

- A missing chat or embedding provider records a **Skipped** run with a setup reason. Recurring tasks stay enabled and retry at their next occurrence after you configure the provider. Start a skipped one-shot manually.
- A configured provider with missing credentials, initialization trouble or an execution error records a **Failed** run. So does a run that reaches the 40-step tool limit before it finishes.
- Tasks added by plugins keep their settings and history when the plugin is disabled or removed. Its tasks record **Skipped** with a reason until you enable or reinstall the plugin and restart, whatever their instruction mode. You can still edit, pause or delete it.
- After a restart, tasks wait until their owner unlocks the Vault Password. Locked attempts invoke no handler, tool or hook and create no run record. Tasks keep their enabled state and resume after unlocking, and a persistent task keeps its earlier session.

Notifications use a content-free body in the in-app inbox. Open the task to read its result.

## Low battery skips

Turn on **Skip scheduled tasks on low battery** in **Settings > Preferences > General** to protect automatic scheduled and chained starts. It starts off. **Battery threshold (%)** accepts whole numbers from 1 through 100, with a default of 10.

- When the computer running Oscova has a detected battery below the threshold and is confirmed disconnected from external power, the occurrence is recorded as **Skipped** with a linked inbox notice. A skipped parent starts no children, and each chained child checks fresh host facts before starting.
- Recurring tasks stay enabled for their next occurrence. Plugging in does not retry, and a skipped one-shot needs **Run now**. Manual and webhook starts stay available, and a running task is not stopped.
- Unknown power facts permit execution. The setting cannot be turned on until battery, percentage and external-power facts are known.

## Related articles

- [Scheduling](references/user-guide-automation-scheduling.md)
- [Monitoring runs](references/user-guide-automation-monitoring-runs.md)
- [Tool governance](references/user-guide-concepts-tool-governance.md)
- [Instructions](references/user-guide-knowledge-and-files-instructions.md)
