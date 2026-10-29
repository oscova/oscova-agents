# Hooks

Hook status appears with its name. Handler type and timeout appear with the target. Narrow screens show labelled groups.

Hooks connect Oscova events to commands, HTTP endpoints, and task webhook callers.

> **In plain terms:** outbound hooks call another system. Task webhooks let another system start an Oscova task.

## Configure outbound hooks

- Click **Add hook** or edit an existing hook to configure its handler and events.
- **Task start**, **User prompt submit**, and **Before tool use** can block work.
- Hook infrastructure failures deny blocking events and allow observe-only events before individual **Failure mode** settings can apply.
- Click **Save hooks** after visual edits or after **Apply JSON**.

> **Caution:** only an Admin can author command hooks. They run unsandboxed in the Oscova host process with its full environment, which grants script-level access to the machine. Hook payloads can also contain sensitive run data. Trust every command and HTTP destination.

- Hooks receive identity and status fields without prompt text, attachments, tool arguments or task result content.

## Manage access and secrets

- Click **Create API key** for external task triggers.
- Disable or delete a key when an integration should stop.
- Unlock the task owner's Vault Password when a task webhook returns **409 Conflict** with an unlock message.
- Save outbound HTTP secrets, then select them from **Secret headers**.
- Remove a saved hook reference before deleting its secret.

## Related

- [Agent Tasks](agent-tasks.md) - enable webhook triggers on tasks.
- [Credentials](settings.credentials.md) - manage user-scoped secrets.
- [Configure outbound hook](dialog.hook-editor.md) - help for this dialog.
- [Create task webhook API key](dialog.hook-api-key-editor.md) - help for this dialog.
- [Save outbound hook secret](dialog.hook-secret-editor.md) - help for this dialog.
