# Metrics dashboard

The metrics dashboard gives you a 30-day view of how your agents are being used. It combines chat usage, model provider usage, tool activity, task runs, estimated cost and audited egress events in one place.

> **Note:**
> **Metrics** are usage counts recorded by Oscova Agents. They help you spot workload, cost, reliability and privacy patterns without opening every chat or task run.



## Open the dashboard

Open **Dashboard** from the main shell. The page header shows **Activity over the last 30 days** and a **Last 30 days** badge.

- If there is no activity yet, the dashboard shows **No metrics yet**. Run a chat or an agent task, then return after the run finishes.
- Oscova caches metrics snapshots to keep dashboard reads fast. If a run you just completed is missing, wait for the next aggregation cycle, normally about one minute, then refresh the page. Refreshing does not itself force a new aggregation.

## Read the summary cards

- **Total tokens:** input and output token volume.
- **Estimated cost:** reported input tokens times the saved input rate plus reported output tokens times the saved output rate. Rates are USD per million tokens and are captured with each run.
- **Egress:** audited data-leaving events. Select this card to open the Egress Audit details.
- **Tool calls:** successful and failed tool invocations.
- **Task runs:** scheduled, manual, webhook and chained task execution activity.
- **Model providers:** configured provider count, with the agent count beside it.

> **Note:**
> A **token** is a small piece of text counted by a model. Input tokens come from prompts and context. Output tokens come from model responses.

The cost estimate uses standard rates. Cached input uses the input rate, other provider charges are excluded and reported usage may omit billable tokens.

- **Partial estimate** means at least one recorded usage entry lacks a needed price or has tokens outside its reported input and output counts. Priced entries still contribute to the displayed subtotal.
- **Unavailable** means no recorded usage can be priced.
- A fully priced zero-rate run shows a real zero.

## Review the charts and panels

The **Usage overview** chart shows input tokens, output tokens and egress by day. Use it to notice spikes, quiet days and whether egress rises with a particular workload.

The **Operational status** panel shows task success rate, average task duration and fallback activations. Fallback activations count runs where the fallback successfully starts a response. An attempt that fails before responding does not count.

Lower panels break usage down by:

- Provider, agent and model token totals.
- Provider cost totals.
- Tool success and failure counts.
- Usage distribution and input/output token mix.

When many providers, agents, models or tools exist, the dashboard shows the top rows and groups the remaining tail as **Other**.

## Understand egress on the dashboard

**Egress** counts recorded cloud-provider calls, Internet-classified tool calls, connected-channel replies and cloud embedding activity for conversation memory or Library. The count is the number of audited egress events in the 30-day window, not a token count.

Providers classified as local, including trusted LAN endpoints, do not add provider egress. An empty egress count is not proof that every operation stayed on this machine. Local-only work can still contribute to token, tool and task activity when those metrics are recorded.

> **Tip:**
> Select the **Egress** card when you need the concrete event list behind the count. The audit dialog shows destinations, source type, agent context and related IDs.

## Admin view

Regular users see their own activity. Admins see a system-wide snapshot for the main cards and charts, plus **Admin usage by user** and **Storage by user** panels.

- The per-user panels show which account produced token and egress volume, how much app-data storage each user folder occupies and how many files it contains.
- The system-wide charts group other users' own providers, models, agents and MCP tools as **Users' own providers**, **Users' own models**, **Other users' agents** and **Users' own tools**. Providers the operator shares and built-in or plugin tools keep their names.
- When an admin opens the Egress Audit from the dashboard, the audit can show system-wide rows and includes a user column.

## Troubleshooting

- **No metrics yet:** run a chat or agent task and wait for it to finish.
- **Recent activity is missing:** refresh the page or wait briefly for the cached dashboard snapshot to update.
- **Estimated cost is lower than expected:** check both prices on the model provider and whether the provider reported all billable tokens. Storage and paid tools are outside this estimate.
- **Egress is zero:** the recent work may have used only local providers and local-only tools, or no audited egress events were recorded in the last 30 days.
- **A tool failure appears in tool calls:** open the related chat, task run or application logs for the detailed error.
- **The dashboard cannot load:** refresh the page. If it still fails, check application logs for dashboard or metrics errors.

## Related articles

- [Egress audit](references/user-guide-monitoring-egress-audit.md)
- [Session inspector](references/user-guide-monitoring-session-inspector.md)
- [Monitoring runs](references/user-guide-automation-monitoring-runs.md)
- [Model providers](references/user-guide-concepts-model-providers.md)
