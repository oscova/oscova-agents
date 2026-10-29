# Runtime hooks

Hooks connect agent lifecycle events to policy checks, audit systems, and automation. Oscova has three related surfaces with different trust and data-flow directions:

| Surface | Direction | Purpose |
| --- | --- | --- |
| Runtime hook contracts | Inside an embedding host | Represent lifecycle events and normalized allow or deny results. |
| Configured command and HTTP hooks | Outbound from Oscova | Send selected runtime events to an administrator-controlled process or HTTP endpoint. |
| Task webhooks | Inbound to Oscova | Let an authenticated external caller start a webhook-enabled task and poll its status. |

Do not treat a task webhook as a runtime hook handler. It starts work through an HTTP API. A configured runtime hook observes or blocks work that Oscova is already preparing to perform.

## Use the public runtime contracts

The reusable `Oscova.Agents` project exposes the runtime hook vocabulary under `Oscova.Agents.Hooks`.

An embedding host sends a `HookDispatchRequest` to `IAgentHookDispatcher.DispatchAsync`. The request contains:

- A `HookEnvelope` with the event and its context.
- `IsBlocking`, which tells the caller whether a deny decision can stop that lifecycle event.

The dispatcher returns a `HookDispatchResult`. Its `Decision` is `Allow` or `Deny`. `Reason` and `Message` carry optional machine-readable and human-readable details. `IsTransportFailure` distinguishes handler failure from an explicit policy decision.

The public contract does not choose command or HTTP transport for a custom host. Your host owns handler selection, ordering, timeout, and failure mapping. The configured Oscova host is one implementation of this contract.

### Event set and blocking behavior

| `HookEventKind` | When it occurs | Blocking |
| --- | --- | --- |
| `TaskStart` | Before a scheduled, manual, chained, or externally triggered task begins work. | Yes |
| `TaskComplete` | After a task completes successfully. | No |
| `TaskFailed` | After a task fails, is cancelled, or is denied. | No |
| `SessionStart` | When a new agent conversation session starts. | No |
| `UserPromptSubmit` | After the user submits a prompt and before model execution. | Yes |
| `PreToolUse` | Before a tool invocation. | Yes |
| `PostToolUse` | After a tool succeeds, fails, or is denied before invocation. | No |

For a blocking request, an embedding host should stop the action when `HookDispatchResult.IsAllowed` is `false`. `HookDeniedException` is the public run-abort exception for carrying the event, optional tool name, message, and reason through an agent run.

Observe-only does not mean fire-and-forget. Configured handlers are awaited in order and can add latency, but their decisions do not reverse an action or fail a completed lifecycle stage.

### Implement a dispatcher

This minimal dispatcher blocks one tool at the public contract boundary:

```csharp
using System.Text.Json;
using Oscova.Agents.Hooks;

public sealed class ShellGuardDispatcher : IAgentHookDispatcher
{
    public Task<HookDispatchResult> DispatchAsync(
        HookDispatchRequest request,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var payload = request.Envelope.Event;
        var blocksShell =
            request.IsBlocking &&
            request.Envelope.HookEventName == HookEventKind.PreToolUse &&
            payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("tool_name", out var toolName) &&
            toolName.GetString() == "shell__exec";

        return Task.FromResult(blocksShell
            ? HookDispatchResult.Deny(
                "Shell execution is disabled by host policy.",
                "shell_disabled")
            : HookDispatchResult.Allow());
    }
}
```

Honor the supplied cancellation token. Caller cancellation is not a hook decision and should propagate to the run.

## Read the envelope

Configured command and HTTP handlers receive schema version 1 as snake-case JSON. Null optional properties are omitted:

```json
{
  "schema_version": 1,
  "hook_event_name": "UserPromptSubmit",
  "hook_run_id": "4d2f1e0c2a3b4f6e8a9b0c1d2e3f4a5b",
  "timestamp": "2026-07-30T10:00:00.0000000+00:00",
  "event": {
    "chat_thread_id": "thread-id",
    "agent_id": "main",
    "execution_kind": "web_chat"
  },
  "user_id": "user-id",
  "agent_id": "main",
  "chat_thread_id": "thread-id",
  "execution_kind": "web_chat"
}
```

The top-level fields are stable across events:

| Field | Meaning |
| --- | --- |
| `schema_version` | Envelope schema version, currently `1`. |
| `hook_event_name` | A string value from `HookEventKind`. |
| `hook_run_id` | A unique identifier for this hook event dispatch. Matching handlers receive the same value. |
| `timestamp` | UTC creation timestamp. |
| `event` | Event-specific JSON payload. |
| `user_id`, `agent_id` | Associated identifiers when available. |
| `chat_thread_id` | Present for chat activity. |
| `task_id`, `task_run_id` | Present for task and task-tool activity. |
| `execution_kind` | `web_chat`, `channel`, `task_run`, or `unknown`. |

For channel sessions and prompts, `channel_id` appears inside `event`, rather than as a top-level envelope field.

Event payloads contain:

| Events | Event-specific fields |
| --- | --- |
| `SessionStart` | `chat_thread_id`, `agent_id`, `execution_kind`, `channel_id` |
| `UserPromptSubmit` | `chat_thread_id`, `agent_id`, `execution_kind`, `channel_id` |
| `PreToolUse`, `PostToolUse` | `tool_name`, `tool_call_id`, `result_kind`, and chat or task context |
| `TaskStart`, `TaskComplete`, `TaskFailed` | `task_name`, `status`, `trigger_source` |

`PostToolUse.result_kind` is `succeeded`, `failed`, or `denied` when present. The payload reports the outcome without error content or the tool's returned value.

All agents keep identity and lifecycle fields while omitting content. UserPromptSubmit omits prompt_text and attachments. Tool events omit arguments, error_message and reason. Task events omit parameters, result_summary and error.

> **Warning:**
> Hook envelopes contain identity and lifecycle fields without content. Treat every configured process and HTTP destination as a trusted recipient.

## Configure outbound handlers

The Hooks settings page stores system-wide command and HTTP handlers. A handler must be enabled, select at least one event, and provide the options required by its handler type.



Matching handlers execute sequentially in their saved order:

1. Disabled handlers and handlers that do not select the current event are skipped.
2. A blocking dispatch stops at the first explicit or fail-closed denial. Later matching handlers do not run.
3. A blocking allow or fail-open transport failure continues to the next handler.
4. Observe-only dispatch runs every matching handler. Deny results do not stop the lifecycle.
5. No matching handlers produces an allow result.

The default timeout is 10 seconds. Configured values are accepted from 1 through 300 seconds. An out-of-range stored value normalizes to 10 seconds. A handler timeout is a transport failure. Caller cancellation remains cancellation and is not converted to allow or deny.

### Command handlers

A command handler can be authored only through the administrator-controlled Hooks settings surface. It starts the configured executable directly with its argument list. It does not invoke the agent's shell tool and does not run in the agent command sandbox. The child process runs on the host, inherits Oscova's full host environment, and can use the configured working directory.

> **Warning:**
> A configured command handler has script-level access to the machine under Oscova's account. This unsandboxed Host authority is intentional. Treat every command as administrator-authored executable code.

Oscova writes one JSON envelope to standard input and then closes the stream. It also supplies:

| Environment variable | Value |
| --- | --- |
| `OSCOVA_HOOK_EVENT_NAME` | Event name. |
| `OSCOVA_HOOK_RUN_ID` | Individual dispatch identifier. |
| `OSCOVA_HOOK_SCHEMA_VERSION` | Envelope schema version. |
| `OSCOVA_HOOK_TIMESTAMP` | Envelope timestamp in round-trip format. |
| `OSCOVA_HOOK_USER_ID` | User identifier, when available. |
| `OSCOVA_HOOK_AGENT_ID` | Agent identifier, when available. |
| `OSCOVA_HOOK_CHAT_THREAD_ID` | Chat thread identifier, when available. |
| `OSCOVA_HOOK_TASK_ID` | Task identifier, when available. |
| `OSCOVA_HOOK_TASK_RUN_ID` | Task run identifier, when available. |
| `OSCOVA_HOOK_EXECUTION_KIND` | Runtime channel. |

If standard output contains a valid decision object, that decision takes precedence over the process exit code. Nonblank malformed JSON or an object without a decision is an `invalid_response` transport failure. Only empty standard output falls back to the exit code:

| Exit code | Result |
| --- | --- |
| `0` | Allow |
| `2` | Deny, using standard error or standard output as the message |
| `3` | Deny because deferral is unsupported |
| Any other non-zero value | Transport failure |

Write only the decision JSON to standard output. Send diagnostics to standard error so extra text does not make the JSON unusable. On timeout, Oscova attempts to terminate the entire child process tree before applying the configured failure mode.

### HTTP handlers

An HTTP handler requires an absolute URL and always sends `POST` with the envelope as JSON. In addition to configured static and secret headers, the request includes:

- `X-Oscova-Hook-Event-Name`
- `X-Oscova-Hook-Run-Id`
- `X-Oscova-Hook-Schema-Version`
- `X-Oscova-Hook-Timestamp`
- `X-Oscova-Hook-User-Id`
- `X-Oscova-Hook-Agent-Id`
- `X-Oscova-Hook-Chat-Thread-Id`
- `X-Oscova-Hook-Task-Id`
- `X-Oscova-Hook-Task-Run-Id`
- `X-Oscova-Hook-Execution-Kind`

Optional context headers are omitted when their value is unavailable.

Static header values are stored with the hook configuration. Header names must be nonblank and unique across static and secret headers.

Secret headers store a header name and credential reference. The plaintext is resolved only when the request is sent. If a referenced secret or field has no value, Oscova returns a `missing_secret` transport failure without sending the HTTP request. The configured failure mode then determines whether a blocking event continues or is denied. Configure the referenced system credential before enabling the handler.

## Return a decision

Command standard output and successful HTTP responses accept the same case-insensitive decision values:

```json
{ "decision": "allow" }
```

```json
{
  "decision": "deny",
  "reason": "policy_denied",
  "message": "This action is blocked by policy."
}
```

`reason` and `message` are optional. `defer` is recognized but unsupported, so it becomes a deny result with reason `deferral_not_supported`.

Response handling is transport-specific:

- A successful HTTP response with an empty or whitespace-only body allows.
- A nonblank malformed JSON response, JSON `null`, or an object with a missing or blank decision is an `invalid_response` transport failure.
- A successful HTTP response with an unsupported decision value is a transport failure.
- A non-success HTTP status is a transport failure. Its response body or reason phrase becomes the diagnostic message.
- Only empty command standard output falls back to the process exit code. Invalid nonblank output is a transport failure even when the process exits with `0`.

An explicit deny always denies a blocking event, regardless of `HookFailureMode`. Failure mode applies only to transport or handler failures:

- `FailOpen` converts the failure to allow.
- `FailClosed` converts the failure to deny.

If the configured Oscova host cannot create its dispatcher scope, no handler or per-hook failure mode is available. That infrastructure failure denies blocking events and allows observe-only events.

Transport failures and handler exceptions are logged with the hook and event context. Blocking denials preserve the handler message and reason so chat or task execution can report why work stopped. For observe-only events, failures are logged but do not change the run outcome.

## Validate external-trigger parameters in an embedding host

Use `TaskExternalTriggerValidator` when your host accepts values for an `AgentTaskDefinition` with an enabled external trigger. The validator takes the task definition and an `IReadOnlyDictionary<string, JsonElement>`. It matches supplied names without regard to case, checks each declared JSON value kind and required parameter, clones accepted values, and returns them as an `IReadOnlyDictionary<string, JsonElement>` keyed by the declared names.

Validation failures throw `TaskExternalTriggerValidationException`. Use its `FailureKind`, `ParameterName`, and `ExpectedKind` properties as neutral facts. Your host maps those facts to its own response status and wording.

Pass the validated values to `TaskInstructionRenderer`. It replaces exact `{{parameters.<name>}}` placeholders in one pass and leaves unrelated placeholders unchanged. Replacement values are literal data, so placeholder-shaped text inside a value is not expanded again. Strings render without JSON quotes. Other JSON values, including objects and arrays, use their raw JSON representation.

```csharp
using System;
using System.Collections.Generic;
using System.Text.Json;
using Oscova.Agents.Tasks;

var task = new AgentTaskDefinition
{
    Instruction = "Deploy {{parameters.branch}} with {{parameters.options}}.",
    ExternalTrigger = new TaskExternalTrigger
    {
        IsEnabled = true,
        Parameters =
        [
            new TaskExternalParameter
            {
                Name = "branch",
                Kind = TaskExternalParameterKind.String,
                IsRequired = true
            },
            new TaskExternalParameter
            {
                Name = "options",
                Kind = TaskExternalParameterKind.Json
            }
        ]
    }
};

var supplied = new Dictionary<string, JsonElement>
{
    ["BRANCH"] = JsonSerializer.SerializeToElement("main"),
    ["options"] = JsonSerializer.SerializeToElement(new { dryRun = true })
};

try
{
    var validated = TaskExternalTriggerValidator.ValidateParameters(task, supplied);
    Console.WriteLine(TaskInstructionRenderer.Render(task.Instruction, validated));
}
catch (TaskExternalTriggerValidationException exception)
{
    switch (exception.FailureKind)
    {
        case TaskExternalTriggerValidationFailureKind.TriggerDisabled:
        case TaskExternalTriggerValidationFailureKind.UnknownParameter:
        case TaskExternalTriggerValidationFailureKind.ParameterKindMismatch:
        case TaskExternalTriggerValidationFailureKind.RequiredParameterMissing:
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                exception.FailureKind,
                exception.ParameterName,
                exception.ExpectedKind
            }));
            break;
        default:
            throw new ArgumentOutOfRangeException(nameof(exception));
    }
}
```

These framework rules do not authenticate callers, authorize task access, define an endpoint, queue work, schedule runs, or execute the task. The embedding host owns those responsibilities.

## Call inbound task webhooks

Inbound task webhooks are an authenticated task API, not outbound handlers. The target task must exist, be enabled, and have **Allow external webhook trigger** enabled.

```http
POST /api/hooks/tasks/{slug}/runs
Authorization: Bearer <token>
Content-Type: application/json

{
  "parameters": {
    "customer": "Contoso"
  }
}
```

The `{slug}` is the complete owner-qualified task slug, such as `{ownerUserId}-nightly-build`, using the existing 80-character URL normalization. The task editor displays the complete endpoint. The bearer token must be an enabled task webhook key with the `tasks:run` scope, and its allowlist must contain that complete normalized slug. Empty allowlists authorize no tasks. Duplicate names are checked within the owner's unlocked definitions, and a locked owner remains unavailable to webhook callers.

The plaintext token is shown only when the key is created. Later management responses expose metadata and a prefix, not the plaintext token or its stored hash.

Trigger responses use:

| Status | Meaning |
| --- | --- |
| `202 Accepted` | The scheduler accepted the run and returned `taskId`, `runId`, `status`, `message`, and a relative `statusUrl`. |
| `400 Bad Request` | Supplied parameters are missing or do not match the task's external parameter definitions. |
| `401 Unauthorized` | The bearer token is missing, invalid, deleted, or disabled. |
| `403 Forbidden` | The valid key lacks `tasks:run` or does not allow the normalized task slug. |
| `404 Not Found` | The task is unavailable, disabled, or not enabled for external triggers. |
| `409 Conflict` | The task owner's vault is locked, the slug is ambiguous, or the scheduler cannot start another run. |

Acceptance is asynchronous. A blocking `TaskStart` hook runs when task execution begins, after the webhook has returned `202`. If that hook denies the task, the run is persisted as failed. The task owner can read its error through the authenticated Tasks API. The webhook status endpoint exposes status metadata without result or error content.

Poll the returned URL with the same bearer token:

```http
GET /api/hooks/tasks/{slug}/runs/{runId}
Authorization: Bearer <token>
```

The status response retains taskId, runId, status, triggerSource, startedAt and completedAt. resultSummary and error are omitted without opening encrypted run content. Scope and slug authorization are checked again on every poll.

## API reference

- HookEventKind
- HookEnvelope
- HookDispatchRequest
- HookDispatchResult
- HookDecision
- HookFailureMode
- IAgentHookDispatcher
- HookDeniedException
- TaskExternalTriggerValidator
- TaskInstructionRenderer
- TaskExternalTriggerValidationException
- TaskExternalTriggerValidationFailureKind

## Related articles

- [Agent Runtime](references/developer-guide-runtime-agent-runtime.md)
- [Hooks Settings](references/user-guide-settings-and-admin-hooks.md)
- [Agent Tasks](references/user-guide-automation-agent-tasks.md)
- [Tool Policies](references/developer-guide-governance-tool-policies.md)
