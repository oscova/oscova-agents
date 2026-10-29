# Monitoring runs

Every time an agent task executes, Oscova Agents records a run. Run history helps you confirm what happened, troubleshoot failures and decide whether a task needs adjustment.

> **Note:**
> **Run** means one execution of an agent task, including status, timestamps, result summary, error text, token count and stored conversation messages when available.

## Read the task directory

The Agent Tasks page shows summary metrics for total tasks, running tasks, completed runs and failed or attention-needed work. Each row shows task name, agent, model, schedule, latest run, enabled state and actions.

Status filters include running, completed, failed, orphan, never run and disabled. An orphan task is linked to an agent that no longer exists.

## Open task history

Select a task row to open its details. The details view shows task summary fields, the current state and recent executions. Use **View all** or **View runs** to browse a larger recent run list.



- Task outcome notifications deep-link back to the task and focused run. Opening one selects that task and loads more recent runs so the reported run is visible when it is still in recent history.
- In the runs list, the trash button deletes a finished run and its transcript after you confirm. The newest finished run and a running one cannot be deleted, because the newest finished run sets the next scheduled time.
- To delete older runs automatically, set **Keep last runs** in the task editor. After each run finishes, runs beyond that count are deleted with their transcripts. Leave it empty to keep every run.
- If a task deletes itself while its details are open, the dialog shows **Deleted task** and keeps the loaded result available for **Inspect**. **View all**, **Runs**, **Edit** and **Run now** are disabled because the task no longer exists.

## Inspect a run

Select **Inspect** when a run has stored messages. The inspector shows the full agent conversation for that execution, which is usually the best evidence for why the task succeeded, failed or skipped work.

- All task run content requires the owner's unlocked Vault Password. A task paused by a locked vault has no new run to inspect. Unlock the vault to resume scheduled execution or use **Run now**.
- A missing chat or embedding provider records a skipped run with the required setup action. The task remains enabled, so recurring tasks try again at their next occurrence after setup.
- Missing credentials, provider initialization problems and execution errors remain failed runs.
- When a task fails, read the first model, tool, credential or workspace error before changing the schedule. A missing credential or blocked tool usually needs configuration, not a different interval.

## Respond to failures

- Fix provider or credential settings when the run fails before useful work starts.
- Configure the provider named by a skipped run, then wait for the next schedule or use **Run now**.
- Adjust the instruction if the agent asks for missing details.
- Review tool approvals when automation blocks a tool call.
- Reduce schedule frequency if tasks run longer than their interval.
- Use **Run now** only when the task is not already executing.
- Reassign orphan tasks when the backing agent was deleted.
- Check webhook API key scope and allowed slugs when an external trigger never starts a run.
- Check application logs when a run completed but its outcome notification did not arrive.

> **Tip:**
> A failed one-shot task remains enabled but does not retry automatically. Fix the failure and use **Run now** to retry it.

## Live versus historical state

The row status tells you what is happening now. Run history tells you what happened before. During long-running work, wait for the current run to finish or inspect the active messages before editing the task.

## Related articles

- [Agent tasks](references/user-guide-automation-agent-tasks.md)
- [Scheduling](references/user-guide-automation-scheduling.md)
- [Logs](references/user-guide-settings-and-admin-logs.md)
