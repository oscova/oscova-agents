# Session Inspector

Use the session inspector to review a saved chat or task-run conversation and its audited egress.

> **In plain terms:** egress records cloud-provider, Internet-tool, connected-channel reply, and cloud embedding activity.

## Review a session

- **Conversation** shows the messages, tool calls, and system instruction captured for the run.
- Reasoning supplied by the model appears separately from its answer.
- **JSON** lets you copy or download a versioned evidence record for the whole session.
- **Egress** lists audited events associated with the session.
- Each egress entry shows the destination and whether it came from a provider, tool, or embedding call.

> **Note:** local providers, including trusted LAN endpoints, do not create provider egress rows. No recorded egress does not prove that all work stayed on this machine, and an event does not prove that a call succeeded.

> **Note:** copied or downloaded evidence can contain prompts, tool arguments, results, file names, and private data.

## Related

- [Metrics Dashboard](metrics-dashboard.md) - see egress counts across recent activity.
