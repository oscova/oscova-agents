# Egress Audit

Use the egress audit to review recorded cloud-provider, Internet-tool, connected-channel reply, and cloud embedding activity.

> **In plain terms:** it is a safety log for checking what external service was used and why.

## Review events

- **Last 30 days** matches the dashboard window.
- **All time** lets you browse the full stored audit log.
- Filter by event type, source, or search text.
- Each row shows the time, destination, source, agent, and context.
- Open inspectable chat or task rows in **Session Inspector** to review the saved session evidence.
- Admins opening system-wide metrics also see the user for each row. Other users' rows name only shared providers and built-in or plugin tools.

> **Note:** embedding entries do not have a session to open, and system-wide audit only lets you inspect your own sessions.

> **Note:** providers classified as local include trusted LAN endpoints and do not create provider egress rows. An empty audit does not prove that every operation stayed on this machine.

> **Note:** embedding rows include conversation memory and Library activity. Failed calls can also create audit rows, including public HTTP hooks that time out or lose their connection after dispatch.

> **Note:** audit rows identify an event and destination. They are not copies of every prompt, attachment, or response.

## Related

- [Metrics Dashboard](metrics-dashboard.md) - open this audit from the Egress card.
- [Session Inspector](session-inspector.md) - review egress for one chat or task run.
