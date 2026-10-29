# Agent as tool

Agent-as-tool lets one agent call another exposed agent as a specialized helper. The caller receives a normal AI function. Invoking that function starts a separate run of the called agent.



> **Note:**
> **Agent as tool** means exposing a specialist agent so another agent can call it like a tool for focused work.

## Definition and discovery contract

Two definition properties control delegation:

- `AgentDefinition.IsExposedAsTool` makes a custom agent eligible to be called.
- `AgentDefinition.AgentToolIds` lists the exposed agent IDs a caller may use.

Both conditions are required. `AgentOrchestrator` resolves IDs only from the definitions supplied by the host and builds a reference only when the target still has `IsExposedAsTool` enabled. Oscova supplies definitions visible to the current user. A missing, hidden, or no-longer-exposed target is omitted, and a stale cached instance is removed.

The build walk starts with the requested agent ID in a `HashSet<string>`. Repeated IDs, self-references, and cycles therefore stop at their first visit instead of expanding indefinitely. There is no separate numeric delegation-depth setting: the finite definition graph and this build guard bound construction.

> **Important:**
> `AgentAsToolProvider` itself preserves the supplied ID order and does not remove duplicate IDs. Host editors should persist unique selections, and callers that construct the public provider directly should deduplicate their input.

## Callable name and function contract

`AgentAsToolProvider` creates an AI function for each resolved `ChatClientAgent`. Oscova sets the function name by normalizing the agent display name: each run of characters outside ASCII letters and digits becomes `_`. The agent description remains the function description.

Agent saves reject normalized-name collisions between non-main agents whose user audiences overlap. This prevents two visible agents such as `Release Notes` and `Release-Notes` from both becoming `Release_Notes`.

The reusable provider's function contract is:

- Input: one required `query` string and one optional `conversation` string.
- Output: the called agent's final `AgentResponse.Text`.
- Cancellation: the function passes its cancellation token through to the called run.
- Failure: child-run exceptions are not converted into a success string. They propagate through the caller's function-invocation pipeline.
- Session: a repeated conversation name resumes that child conversation inside the same parent session. A missing or blank name uses the default conversation for that child.

Framework run-option `AdditionalProperties` from the current function-invocation context are copied into the child run. This lets host run metadata continue across the delegation boundary.

### Oscova web-chat destinations

Oscova adds a web-chat wrapper with `destination`, defaulting to `delegated`, and `includeChatHistory`, defaulting to `true`:

- `delegated` runs separate work with the named child conversation described below.
- `chat` consults an agent the user has already invited to the current discussion. Omit `conversation` for this destination.
- With `chat`, `includeChatHistory: false` answers in a fresh session while preserving the retained shared session. The consulted agent still receives its configured memory and skills.

Shared-chat consultations use the agent's saved choice of the caller's chat model or its own configured provider. Separate delegated work uses the child's own provider selection. These destinations are application behavior. The reusable `AgentAsToolProvider` exposes only `query` and `conversation`.

## Conversation lifecycle

Each parent session stores one child session for every called agent and conversation name. The child agent ID is the stable identity. Conversation names are trimmed and compared without case sensitivity, so `Research` and `research` continue the same line of work.

The child session is saved only after a successful call. A failed or cancelled call leaves the last successful snapshot unchanged. If a framework caller invokes the provider without a parent session, every delegated call remains stateless and starts with a fresh child session.

The child keeps its configured compaction behavior. When compaction reduces its history, the reduced conversation is the history that remains available. Function invocation for this provider must stay serial because two calls must not mutate the same child session concurrently.

The Host presents child messages as read-only chat tabs. While a background call runs, its tab refreshes from live progress. Incognito parent chats keep the same continuity in memory, but their parent and child sessions are not written to disk.

## Background work and same-agent subagents

When the provider's `resolveWork` callback returns a `DelegatedWorkState`, agent calls start in the background and return a handle instead of the final text. Without a work state, calls run synchronously as described above.

With a work state and a `createSelfWorker` factory, the parent also receives same-agent subagent tools. A subagent is a separate instance of the parent definition with a fresh history.

| Tool | Purpose |
|---|---|
| `start_subagent` | Starts a subagent with a self-contained `task` and a required `name` of 1 to 40 characters. |
| `subagent_status` | Lists every subagent in the conversation, or shows 1 to 20 recent messages for one handle. Each message is cut to 1,000 characters. |
| `message_subagent` | Sends a message to a running subagent. |
| `interrupt_subagent` | Stops a running subagent. |
| `continue_subagent` | Starts a new run on a finished or interrupted subagent's retained history. |

The parent waits through `WaitToolset` by passing subagent handles to its wait tool. These rules bound the work:

- At most `DelegatedWorkState.MaxActiveWorkers` (4) workers run at once for one work state.
- Workers receive no agent-as-tool or subagent tools, so they cannot start further subagents. They also cannot ask the user questions.
- Every result is delivered to the parent before its run finishes.
- Disposing the work state stops its running workers.

In Oscova, `AgentDefinition.IsSubagentDelegationEnabled` (shown as **Use subagents**, on by default) controls whether the self-worker factory is supplied. Tasks can override it in their run scope.

## Runtime flow

The host prepares an `AgentBuildRequest` with the requested definition, definitions visible to the user, model-client and context-provider factories, workspace resolution, and optional run middleware. `AgentOrchestrator.GetOrCreateAgentAsync()` then:

1. Walks `AgentToolIds` and builds nested exposed dependencies before their parents.
2. Builds every exposed agent from that agent's definition.
3. Adds built exposed agents to `AIAgentCollection`.
4. Builds the requested agent.
5. Lets `AgentAsToolProvider` capture the already-built child instances and expose them as functions.

That last step is a snapshot. A provider does not perform a new collection lookup on every request. This is also important for public use: populate `AIAgentCollection` before constructing `AgentAsToolProvider`.

## Called-agent runtime shape

The called agent does not inherit the caller's entire context. Oscova deliberately rebuilds a restricted sub-agent shape:

| Concern | Delegated behavior |
|---|---|
| Model and model options | Uses the called definition's default model provider, or the app default, plus that provider's chat options. |
| Standing instruction | Uses the called definition's system instruction and its resolved variables. |
| Ordinary tools | Uses the called definition's allowlist and tool policies. |
| Nested agent tools | Uses the called definition's `AgentToolIds`, subject to the same exposure and cycle rules. |
| Workspaces | Resolves the called definition's own grants. In web chat, it also carries the current project and shared run grants, excluding the caller's private folder. Read-only restrictions remain in force. |
| Compaction | Uses the called definition's compaction setting and reducer. |
| Local-only posture | Inherits the initiating run's air gap and can tighten it with the called definition's `IsLocalOnly`. It cannot loosen the caller's posture. |
| Memory and skills | Not attached to delegated sub-agent builds, even when enabled or assigned on the called definition. |
| Private notes, Library, task list, and extra request providers | Attached only to the active requested agent, not the delegated build. |

Calling that same definition directly makes it the active requested agent, so its enabled memory, assigned skills, and other active-agent providers apply normally.

## When to use it

- A support agent delegates billing lookup to a billing specialist.
- A writing assistant asks a research agent to gather source notes.
- A project coordinator asks a scheduler agent to prepare task instructions.

> **Caution:**
> Avoid circular delegation patterns. The builder guard prevents infinite construction, but there is no global runtime delegation-depth counter. Each nested agent run receives its own function-loop limits, so do not use the construction guard as a runtime recursion policy.

## Governance and failure limits

The caller's permission to use an agent tool is not permission to bypass the called agent's policies. `AgentToolProvider` or tool search resolves the called definition's ordinary tools independently:

- `Deny` tools are omitted.
- In Oscova web chat, `RequireApproval` calls reach the interactive approval loop with the called agent's identity. Admission rechecks the delegation chain, current access, workspace grants, and policy before continuing after an approval.
- On surfaces without a child approval route, approval-required tools are omitted. The explicit Host Shell tool is also omitted there, even if its policy is auto-approve.
- Air-gapped runs omit tools classified as network egress.
- Default and auto-approved tools remain available when otherwise allowed.

When the host supplies agent-specific runtime middleware, it is installed on every built agent. Tool hooks, quotas, workspace validation, tool-failure reporting, and accounting therefore record the called agent's identity rather than treating its work as a tool call by the parent.

Every built agent also has the framework function loop capped at 40 iterations per request and three consecutive tool errors, with detailed exception text excluded from model-visible tool results. A nested child invocation is its own request and receives the same per-agent caps.

## Caching and lifetimes

Exposed agents normally live in `AgentOrchestrator.AIAgents` and are reused across builds. The requested agent is cached only when it is not itself exposed, has no request customizers, and the request does not bypass the cache.

Run-specific general builder and options customizers apply only to the requested agent. The per-agent builder customizer applies to exposed agents as well. When present, exposed dependencies are rebuilt and replace their collection entries so host middleware matches the current run. Providers that already captured an earlier child keep that captured instance for their run.

Evict the affected agent or invalidate the agent cache after changing an exposed definition, provider, tools, context, or execution policy. Host save and settings flows perform the relevant invalidation. Integrations that use the public runtime types directly must do so themselves.

## Related articles

- [Custom Agents](references/user-guide-chat-and-agents-custom-agents.md)
- [Shared Agents](references/user-guide-chat-and-agents-shared-agents.md)
- [Tool Policies](references/developer-guide-governance-tool-policies.md)
- AgentDefinition
- AgentBuildRequest
- AgentOrchestrator
- AgentAsToolProvider
- AgentToolProvider
