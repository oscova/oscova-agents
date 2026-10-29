# Hooks

Hooks connect Oscova Agents to other systems. Use them when another app should start an Oscova task, or when Oscova should call another app during an agent run. The Hooks settings are operator-only.

> **Note:**
> An outbound hook is a command or HTTP call Oscova makes after a selected event. A task webhook is an inbound HTTP call another system makes to start an Oscova task.

The Hooks page combines three areas:

- **Hook handlers** for outbound command and HTTP handlers.
- **Task webhook API keys** for callers that trigger tasks. An inbound API key authenticates a caller to Oscova.
- **Outbound HTTP hook secrets** for secret header values Oscova sends to external HTTP hook endpoints.

## Configure hook handlers

1. Select **Add hook**. New hooks start as enabled HTTP hooks for **User prompt submit**, with **Fail closed** and a 10-second timeout.
2. Set the handler fields listed below.
3. Select **Save hooks**. **Reset** discards unsaved handler edits.



The table shows each handler's status, events, target, timeout and actions. Use **Edit** to change a handler, **Duplicate** to copy one and **Delete** to remove one from the draft. The **JSON** tab lets you copy, paste or inspect the handler list. **Apply JSON** validates and replaces only the draft, so select **Save hooks** afterward.

The hook editor has:

- **Name** for the admin label.
- **Handler type** as **Command** or **HTTP**.
- **Enable this hook** to include or skip the handler.
- **Failure mode** as **Fail closed** or **Fail open**.
- **Timeout seconds**, from 1 to 300.
- **Events** that should call the handler.

### Command hooks

Command hooks require **Command**. **Working directory** and one-argument-per-line **Arguments** are optional. Only an operator can author them.

Oscova starts the command directly in the host process, not in the agent's command sandbox. The command inherits Oscova's full host environment and receives the hook envelope through standard input and `OSCOVA_HOOK_*` environment variables.

> **Warning:**
> A command hook grants script-level access to the machine under Oscova's account. Treat it like allowing an operator-authored script to run, and configure only commands you trust.

### HTTP hooks

HTTP hooks require an absolute **URL**. **Static headers** store plain header values in hook settings. **Secret headers** store only a header name and a selected outbound HTTP hook secret.

### What hooks receive

Hook payloads contain identity and lifecycle fields without content. Treat every command and HTTP destination as a trusted recipient.

- Prompt hooks omit prompt_text and attachments.
- Tool hooks omit arguments, error_message and reason.
- Task lifecycle hooks omit parameters, result_summary and error.
- Identity, event, task, tool, status, trigger and execution-kind fields remain available.

## Choose failure behavior

Blocking events can stop the run: **Task start**, **User prompt submit** and **Before tool use**. Session start, task completion or failure and after-tool-use events are observe-only and never stop the run.

- **Fail closed** denies a blocking event when the handler times out, exits unexpectedly, returns a non-success HTTP response, returns malformed non-empty output or otherwise fails. Use it for policy checks.
- **Fail open** lets a blocking event continue after those execution or transport failures. Use it for logging, analytics and notification hooks.

An explicit deny decision still denies a blocking event under either failure mode.

If Oscova cannot create the hook dispatcher or its service scope, no handler can be selected. Blocking events fail closed and observe-only events continue. This infrastructure failure takes precedence over every handler's failure mode.

## Create task webhook API keys

Task webhook API keys let external systems call task webhook endpoints with `Authorization: Bearer <token>`.

1. Enter the required **API key name**.
2. Fill **Allowed task slugs** as a comma-separated allowlist. Copy the complete owner-qualified slug from the task endpoint. An empty list authorizes no tasks.
3. Create the key and copy the token under **Copy this token now**. It is shown once. Later the UI shows only the prefix, created date, last-used date, scopes and allowed slugs.

The key receives the `tasks:run` scope. Allowed task slugs are enforced on every webhook call, so a key with that scope still cannot trigger a task whose slug is not in its list.

Use the key toggle to disable a caller without deleting it. Delete the key when the integration is gone.

Webhook responses include a status URL for the run. External callers can poll that URL to read status, trigger source and timestamps. Summaries and error text remain private and are not returned through the webhook status endpoint.

## Store outbound HTTP hook secrets

Use **Outbound HTTP hook secrets** when an HTTP hook needs a header value that should not be stored directly in hook JSON.

- **Secret name** for the friendly label.
- **Secret reference key** for the stored credential key. If you leave it blank, Oscova derives one from the secret name and adds a `hook-` prefix.
- **Secret value** for the value sent as an HTTP header. New secrets require a value. Existing secrets can be edited with the value left blank to keep the stored value.
- **Description** for admin notes.

Deleting a secret fails when a saved hook still references it. Remove or change those secret headers first.

If a referenced secret cannot be resolved during a hook call, Oscova does not send the HTTP request. It treats this as a dispatch failure, so a blocking event follows the hook's **Fail closed** or **Fail open** setting. After a host restart, unlock the operator vault before hooks need its stored secrets.

## Troubleshooting

- **Save hooks fails:** check that every enabled handler has at least one event and the required command or absolute URL.
- **HTTP hook secret header fails validation:** each header needs a name and selected secret. Duplicate header names are rejected.
- **API key creation fails:** enter a non-blank name and check that allowed slugs are comma-separated.
- **A task webhook returns forbidden:** the key must be enabled, include `tasks:run` and allow the requested task slug.
- **A task webhook returns not found:** the task must exist, be enabled and have its external trigger enabled.
- **A task webhook returns conflict with an unlock message:** unlock the task owner's Vault Password, then retry the webhook.

## Related articles

- [Agent tasks](references/user-guide-automation-agent-tasks.md)
- [Credentials](references/user-guide-settings-and-admin-credentials.md)
- [Runtime hooks](references/developer-guide-runtime-hooks.md)
