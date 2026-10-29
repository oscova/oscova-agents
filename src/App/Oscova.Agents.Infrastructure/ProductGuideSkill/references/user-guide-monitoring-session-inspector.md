# Session inspector

The session inspector shows the saved model conversation for a chat or task run in Oscova Agents. It is the fastest way to review what the agent saw, which tool calls happened and whether the session reached an external service.

> **Note:**
> A **session** is the stored model conversation state for one chat thread or task run. It can include messages, tool calls, tool results, usage details, system instruction and audited egress entries.

## Open the inspector

- From chat, use the session inspector action when the current thread has saved session data.
- From **Agent Tasks**, open a task's details or run history, then select **Inspect** on a run that has stored messages.
- From **Egress Audit**, select **Inspect** on a chat or task-run row to open the related session when it is still available.

If an inspect action is not available, the selected chat or run may not have stored messages yet.



## Use the Conversation tab

The **Conversation** tab shows the readable session:

- An egress summary at the top, which says either **No egress data recorded** or **This session reached the internet**.
- The saved system instruction, when present.
- User, assistant, tool and system messages.
- Reasoning supplied by the model, displayed separately from its answer.
- Tool calls and tool results.
- Usage details when the model response included them.
- Error content when an error was part of the stored message stream.

> **Tip:**
> Start in **Conversation** when diagnosing a failed task. The first model, tool, credential or workspace error usually explains the next fix better than the final status alone.

## Use the Egress tab

The **Egress** tab lists audited egress entries attached to the inspected chat thread or task run. Each entry uses the same row shape as the Egress Audit: time, type, destination, source, agent, context and expandable metadata.

Providers classified as local, including trusted LAN endpoints, do not create provider egress rows. Older sessions may also show no provider egress data if they were created before the current provider egress records existed.

## Use the JSON tab

The **JSON** tab shows the same session evidence in a technical format.

- Use **Copy** when you need to share the session with another developer or compare it with application logs.
- Use **Download** to save a timestamped `oscova-evidence-...json` file. Task-run downloads include the task ID and run ID in the file name. Chat downloads include the thread ID.

The JSON view includes a versioned evidence envelope, the chat thread ID or task and run IDs, `systemInstruction`, an egress summary, `egressEntries` and `messages`.

> **Caution:**
> The inspector is diagnostic evidence. Session JSON can include prompts, tool arguments, tool results, URLs, file names and other operational details. Review it before sharing it outside your team, especially when chats or tools handled private files, credentials, customer data or internal URLs.

## What local-only means here

Local-only work can still appear in the **Conversation** and **JSON** tabs because those tabs show session state. A session can be fully inspectable without having any egress entries.

- A local model can produce an inspectable conversation without a provider egress row.
- A Discord or Telegram reply can still add egress for that session even when the agent ran locally.
- **No egress data recorded** means only that no audit entries were saved. The audit is not a complete network monitor, and entries can include failed calls. See [Egress audit](references/user-guide-monitoring-egress-audit.md) for the coverage boundaries.

## Troubleshooting

- **No messages in session:** the selected chat or run has no stored session messages to display.
- **No egress data recorded:** the session may have used only local resources, or it may be an older session without provider egress records.
- **Inspect from Egress Audit says the session is unavailable:** the original chat thread or task run may have been deleted.
- **Egress failed to load for a task run:** close and reopen the inspector. If it still fails, check application logs for task run or egress audit errors.
- **The copied JSON is too large to review comfortably:** use the Conversation and Egress tabs to locate the relevant message or event first, then copy JSON only when you need exact evidence.
- **A task run failed but messages are missing:** open the task run history and application logs. Some failures happen before a model session is available.

## Related articles

- [Egress audit](references/user-guide-monitoring-egress-audit.md)
- [Metrics dashboard](references/user-guide-monitoring-metrics-dashboard.md)
- [Monitoring runs](references/user-guide-automation-monitoring-runs.md)
- [Agent tasks](references/user-guide-automation-agent-tasks.md)
