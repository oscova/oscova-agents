# Metrics Dashboard

Use the metrics dashboard to review your last 30 days of model, tool, task, and egress activity.

> **In plain terms:** metrics are usage counts that help you spot cost, workload, and reliability patterns.

## Read usage

- **Total tokens** shows input and output token volume.
- **Estimated cost** uses reported input and output tokens with the provider's saved standard USD rates.
- Cached input uses the input rate, while other charges are excluded.
- **Partial estimate** means some recorded usage has a missing rate or tokens outside the reported input and output buckets.
- **Unavailable** means no recorded usage could be priced, while a complete zero-rate run shows a real zero.
- Select **Egress** to open the audit behind its data-leaving event count.
- **Tool calls** counts successful and failed tool invocations.
- **Task runs** summarizes scheduled, manual, webhook, and chained task activity.

> **Note:** egress includes cloud providers, Internet tools, channel replies, and cloud embeddings for memory or Library. Local providers include trusted LAN endpoints, so zero egress does not prove that all work stayed on this machine.

## Review charts

- **Usage overview** charts each day's recorded input tokens, output tokens, and audited egress.
- **Usage distribution** groups top providers and the remaining tail.
- **Token mix** compares input and output token share.
- **Operational status** shows task success rate, average duration, and fallback activations.
- Fallback activations count runs where the fallback successfully starts a response.
- Lower panels break tokens down by provider, agent, and model.

> **Note:** admins also see usage, storage, and file counts by user plus a system-wide snapshot. Other users' own providers, models, agents, and tools appear there as grouped totals. Other users see only their own metrics.

> **Note:** recent activity may take about a minute to appear in the cached dashboard.

## Provider budgets

Budget cards show estimates and limits for providers you own, with the reset time when a limit is reached. Shared totals combine all consumers without exposing their conversations. Other consumers cannot see your budget figures. Incognito usage counts without conversation, task, or agent identity. Amber indicates at least 80% of a limit, and red indicates that new work is blocked. Choose **Edit budget** beside your provider in **Model Providers** to change its limits.

## Related

- [Egress Audit](egress-audit.md) - review data-leaving events in detail.
- [Model Providers](settings.model-providers.md) - set provider cost rates.
