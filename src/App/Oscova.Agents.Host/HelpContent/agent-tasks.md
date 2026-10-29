# Agent Tasks

Run history keeps summaries visible and wraps each record into labelled fields on narrow screens.

The task list groups the agent and model beneath each task. Schedule, latest run, enabled state, and actions remain separate. Smaller screens retain task cards.

Use Agent Tasks to run instructions now or from a schedule, task chain, or webhook.

## Configure tasks

- **New task** opens the task editor for an available agent.
- Edit a task to change its instruction, schedule, notification choices, and run scope.
- Custom task settings replace inherited agent settings for that task only.

> **Note:** Scheduled tasks wait until onboarding completion is saved.

## Built-in tasks

- **Daily Memory**, hourly: saves noteworthy parts of recent chats to today's daily memory.
- **Vector Indexing**, hourly: indexes new chat messages so agents can recall past conversations. It needs an embedding provider in **Settings > Agents & AI > Model Providers**.
- **Dreaming**, 03:00: reviews recent memory and moves stable facts into long-term memory. It skips the model when recent daily memory is empty.
- **Reflection**, 03:00: reviews recent agent behavior and updates each agent's private lessons.
- **Night Shift**, 01:00: works up to three to-do items you delegated to the agent. It skips the model when none are open.
- **Morning Brief**, 07:00: posts a dated brief conversation with a bell notice, covering the night's results and open to-do items.
- Every built-in task except Vector Indexing needs a chat model provider.

## Operate and review

- Use **Refresh** to reload status and recent runs.
- Opening a task notification shows its details once, and closing the dialog keeps it closed when you refresh or toggle tasks.
- Search by name, agent or instruction text, and filter task status.
- Turn on **Show instructions** to preview each task's instructions.
- Enabled cron tasks show their next run time.
- Use the row toggle to pause or resume a task.
- Use **Run now** to start an enabled, idle task.
- Click a row to review recent runs and inspect stored messages.
- A recovered run-history save error does not require you to rerun a completed task.
- A failed task remains enabled and shows **Needs attention** with its latest reason.
- Interval and cron tasks retry at their next occurrence. A failed one-shot stays available for **Run now**.
- Automatic notices go to the app inbox with stock status text and a link to task details. Messages the agent sends through `notification__notify` go to the channel its instructions name, or the web app by default, and hand off the conversation. An agent message and an automatic completion notice can both be sent. Interactive toasts appear only for commands you start on this page.
- Missing chat or embedding setup records a skipped run. Its inbox notice names the task and where to set it up. Recurring tasks stay enabled and try again on schedule.

Tasks added by plugins keep their saved settings and run history when the plugin is disabled or removed. If a task needs code from an unavailable plugin, its next attempted run shows **Skipped** with a reason. Enable or reinstall the plugin and restart the app to restore that execution. You can still edit, pause or delete the saved task.
- Configured credential, provider, or execution problems are recorded as failed runs.
- Run a skipped one-shot manually after fixing provider setup.

After a restart, tasks wait until their owner unlocks the Vault Password. Locked attempts invoke no handler, tool or hook and create no run record. Tasks retain their enabled state and resume through the scheduler after unlocking. Task outcome notifications use generic in-app messages without result or error content.

> **Note:** pausing prevents new runs but does not cancel one already in progress.

## Related

- [Create or edit task](dialog.agent-task-editor.md) - help for this dialog.
- [Task details](dialog.agent-task-details.md) - help for this dialog.
- [Task runs](dialog.agent-task-runs.md) - help for this dialog.
- [Task run scope](dialog.task-run-scope.md) - help for this dialog.
