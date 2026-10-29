# Egress audit

The Egress Audit shows what left your machine: provider calls classified as cloud, tools classified as Internet, connected-channel replies and cloud embedding activity. Use it to answer which external service was used, when, and which chat, task, agent, provider, model or tool was involved.

> **Note:**
> **Egress** describes outbound activity recorded by these integrations. An entry can describe an attempted call and does not by itself prove successful delivery.

## Open the audit

Open **Dashboard**, then select the **Egress** card. The dialog starts on **Last 30 days**, matching the dashboard window. Choose **All time** to browse the stored audit log beyond that window.



Admins opening the audit from a system-wide dashboard can see rows across users. In that mode, the table includes a **User** column. Other users' rows show the time, type, source and user. They name a provider and model only when the operator shares that provider, otherwise the destination reads **Own destination**. A tool is named only when it is built in or comes from an installed plugin.

## Read the summary

- **Total events:** all matching egress rows for the active filters.
- **Provider events:** chat or task model provider calls.
- **Tool events:** internet-connected tool calls.
- **Embedding events:** conversation-memory or Library indexing and search calls that used a cloud embedding provider.

> **Note:**
> Providers classified as local do not create provider egress rows. Local includes trusted LAN endpoints, so an empty audit does not prove that every operation stayed on this machine.

## Filter events

- **Range:** **Last 30 days** or **All time**.
- **Kind:** all kinds, provider, tool or embedding.
- **Source:** all sources, chat, task, memory indexing or memory search.
- **Search egress:** matches event metadata such as thread, run, task, agent, provider, model, tool, kind and source. In admin system scope, it matches only what other users' rows show, plus owner information. Press **Enter** or select **Search**.

**Refresh** reloads the current page and filters. The dialog shows 50 rows per page.

## Read event rows

| Column | Meaning |
| --- | --- |
| **Time** | Local time when the event was recorded |
| **Type** | Provider, tool or embedding |
| **Destination** | The tool name, or the provider and model when available |
| **Source** | Chat, task, memory indexing or memory search |
| **Agent** | The agent associated with the event when known |
| **Context** | The thread ID or task ID |
| **User** | Shown only for admin system-wide review |
| **Inspect** | Shown for chat and task-run rows when the related session is available to you |

Expand a row to see the thread ID, run ID, task ID, provider ID, model and tool. Use these IDs to match an audit event to a specific chat thread, scheduled task run or provider configuration.

Rows show current names, so a renamed agent or provider appears under its new name. An agent or provider deleted after the event reads **Deleted agent** or **Deleted provider**.

## Inspect a related session

Rows tied to a chat thread or task run can open the session inspector directly from the audit table. Use **Inspect** to move from the egress event to the saved conversation, tool calls, system instruction and evidence JSON behind it.

Embedding rows have no chat or task session to inspect. In system-wide admin scope, you can inspect only sessions owned by your current account. Other users' rows remain visible for audit, but their private session transcript is not opened from this dialog.

## What appears here

The audit records provider, tool and embedding egress events, for example:

- A chat or task using a hosted model provider.
- A task using an internet-connected tool.
- A Discord or Telegram run sending its outbound reply.
- Conversation-memory or Library indexing and search using a cloud embedding model.

Keep these boundaries in mind when reading the ledger:

- Inbound Discord and Telegram gateway, polling and attachment traffic does not appear. Only the outbound reply carries run output to the channel service.
- Local provider activity can appear in dashboard token totals without creating a provider egress event. Connected-channel replies appear even when the agent uses a local model.
- The **Memory indexing** and **Memory search** source labels also cover Library embedding work.
- Tool events include successful and failed invocations, and a provider event can be recorded before its request finishes. An event does not prove that a remote call succeeded.
- The audit is not a complete network monitor. Network access by host commands, network-enabled containers or tools declared **Can reach network** is not captured as a complete list of destinations. Review those permissions alongside the audit.

> **Caution:**
> The audit tells you that egress happened and records the related destination and context. It is not a full copy of every prompt, attachment or response sent to an external service.

## Troubleshooting

- **No audit entries match the current filters:** switch to **All time**, clear search text or choose **All kinds** and **All sources**.
- **Inspect is not shown:** the row may be an embedding entry, may not have a saved thread or run ID or may belong to another user in system-wide admin scope.
- **Inspect says the session is unavailable:** the related chat thread or task run may have been deleted after the audit event was recorded.
- **Expected local work is missing:** local-only model work is intentionally excluded from egress.
- **Expected cloud work is missing:** clear the filters, refresh the audit and check application logs for egress audit warnings.
- **Destination says unknown:** the event was recorded without a provider, model or tool label. Expand the row and use the available IDs to continue tracing.
- **The audit cannot load:** refresh the dialog. If it still fails, check application logs for egress audit errors.

## Related articles

- [Metrics dashboard](references/user-guide-monitoring-metrics-dashboard.md)
- [Session inspector](references/user-guide-monitoring-session-inspector.md)
- [Monitoring runs](references/user-guide-automation-monitoring-runs.md)
- [Logs](references/user-guide-settings-and-admin-logs.md)
