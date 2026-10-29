# Context providers

A Microsoft Agent Framework `AIContextProvider` adds invocation-specific instructions, messages, or tools without changing the agent definition. Use a provider when context must be assembled at run time. Use a tool when the model should decide whether to perform an action during the conversation.

Oscova's public providers include EnvironmentContextProvider, AgentPersonalContextProvider, AgentToolProvider, ToolSearchProvider, AgentAsToolProvider, VisionToolProvider, and MemoryContextProvider.

> **Important:**
> Provider output is trusted model input. Microsoft Agent Framework does not sanitize instructions or messages returned by a provider. Treat files, memory, search results, remote data, and tool output as untrusted data rather than instruction authority.

## Follow the provider lifecycle

For each agent invocation, Microsoft Agent Framework processes the configured provider list sequentially:

1. Start with the run's current instructions, input and history messages, and tools.
2. Call the first provider's `InvokingAsync`.
3. Use that provider's merged `AIContext` as input to the next provider.
4. Continue in list order, then send the final instructions, messages, and tools to the chat client.
5. After the model completes or fails, call each provider's `InvokedAsync` in the same configured order.

The base `AIContextProvider` implementation gives `ProvideAIContextAsync` the accumulated instructions and tools. Its default message filter exposes only messages whose source is `External`, not chat-history or earlier-provider messages.

When a run continues an existing model response through `ChatOptions.ContinuationToken`, the framework skips pre-run context-provider assembly for that continuation.

When `ProvideAIContextAsync` returns, the base implementation:

- joins existing and new instructions with one newline.
- appends provider messages and stamps them with the provider type as their source.
- appends provider tools.
- preserves the previous value when the returned property is null.

The merge does not deduplicate tools or messages. Order providers deliberately and avoid contributing the same tool twice.

At the end of a successful invocation, the default `InvokedCoreAsync` filters request messages to external messages, keeps all response messages, and calls `StoreAIContextAsync`. On a failed invocation, it skips `StoreAIContextAsync`. Override `InvokedCoreAsync` only when the provider must inspect failures or control the default filtering itself.

## Return a valid context

`ProvideAIContextAsync` must return a non-null `AIContext`.

- Return `new AIContext()` when the provider has nothing to add.
- Leave `Instructions`, `Messages`, or `Tools` null when that category has no contribution.
- Return empty message or tool collections only when an empty sequence is meaningful to your own code. The default merge treats them as no additions.
- Do not return null and do not include null provider entries in an `AgentBuildRequest` provider list.

A provider exception or cancellation stops the pre-run pipeline. Later providers and the model are not invoked. A post-run provider exception also propagates and prevents later post-run providers from running. Honor the supplied cancellation token in file, database, HTTP, and other asynchronous work.

## Create a provider

This provider adds a trusted, host-generated instruction. It returns an empty context when no note applies.

```csharp
using Microsoft.Agents.AI;

public sealed class RuntimeNoteProvider(string? trustedNote) : AIContextProvider
{
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var result = string.IsNullOrWhiteSpace(trustedNote)
            ? new AIContext()
            : new AIContext
            {
                Instructions = $"Runtime note: {trustedNote}"
            };

        return ValueTask.FromResult(result);
    }
}
```

Add the instance at the intended position in the ordered list returned by `AgentBuildRequest.ContextProviderFactory`. For asynchronously loaded context, await the source with `cancellationToken` and use `ConfigureAwait(false)` in library code.

Do not put raw retrieved text into `Instructions`. If untrusted data must be model-visible, bound its size, label and delimit it as data, validate its origin, and keep authorization in application or tool boundaries.

## Keep provider state session-scoped

One provider instance can serve many invocations and sessions. Do not store current-user, current-message, or per-session state in ordinary provider fields.

Use `AgentSession.StateBag` when state must follow an agent session. Every provider has `StateKeys`. The default key is the concrete type name. Agent construction throws `InvalidOperationException` when:

- two context providers share a state key.
- a context provider shares a state key with the chat-history provider.

Override `StateKeys` with stable, unique values when attaching multiple instances of the same provider type.

`AgentOrchestrator` does not dispose context providers when an agent is evicted. Prefer provider instances backed by host-managed services and avoid per-build owned disposables unless the embedding host has an explicit lifetime owner.

## Understand Oscova's default order

The configured Oscova host assembles providers in this order. Conditions are evaluated separately for the requested agent and any exposed agent-tool dependency.

| Order | Context source | Contribution | Condition |
| --- | --- | --- | --- |
| 1 | Environment | Instructions | Always. Resolves runtime facts and current workspace grants. |
| 2 | Tool exposure | Tools, plus instructions in tool-search mode | Always. Uses either direct selected-tool exposure or `search_tools`. |
| 3 | Agent instruction and personal variables | Instructions | Always. A blank instruction contributes nothing and does not call the template model factory. |
| 4 | Browser | Instructions | Available browser capability, eligible selected browser tools, and network access permitted. Browser tools are available in web chats. |
| 5 | Private notes | Instructions | Requested active agent only, and never a shared definition. Missing or unauthorized notes contribute nothing. |
| 6 | Called agents | Tools | Added when the definition has `AgentToolIds`. Missing or unavailable built agents are omitted. Web chat supports child approvals and consultation destinations. |
| 7 | Skills | Instructions and tools | Requested active agent only, with a user ID and at least one assigned skill. |
| 8 | Memory | Instructions and tools | Requested active agent only, with a user ID and memory enabled. |
| 9 | Library | Instructions and tools | Requested active agent only, with a user ID and the resolved read-only Library grant. |
| 10 | Todo list | Instructions, a synthetic user message, and tools | Requested active agent only when todo support is enabled. |
| 11 | Caller-supplied run providers | Provider-defined | Requested active agent only. The configured host uses this position for capabilities such as vision. |

The public `AgentBuildRequest.ContextProviderFactory` is more general than the configured host. An embedding host controls the entire list and may return a different order for each definition.

## Know the public provider behavior

### Environment context

`EnvironmentContextProvider` renders these Scriban variables:

- `working_directory`
- `os`
- `architecture`
- `shell`
- `path_separator`
- `home_directory`
- `temp_directory`
- `workspace_directories`
- `private_directories`
- `has_private_directory`
- `workspace_has_aliases`
- `shell_root_variables`
- `has_shell_roots`
- `uses_host_paths`
- `host_os`
- `host_architecture`
- `host_shell`
- `host_path_separator`

Use `EnvironmentContextProvider.FromWorkspaceResolver` when grants can change between invocations. The resolver is called once per render. With no directories, `workspace_directories` renders `- none configured` and the default host-derived `working_directory` is `not available`. Otherwise the working directory uses the preferred writable grant, falling back to the first supplied directory.

The canonical `/self` grant renders through `private_directories`, separately from `workspace_directories`. The default template includes **Your Private Folder** and its ownership guidance only when `has_private_directory` is true. Both lists use canonical aliases. `shell_root_variables` pairs each named environment variable with its alias. The host exports actual native or mounted values through the execution-session environment. `ShellDirectories` restricts these variables to the executing device, so node shells receive no server-root variables.

`RuntimeEnvironment` describes the normal shell target, and `HostEnvironment` describes explicit host execution. Oscova supplies Linux/container facts in Docker execution and the actual host shell facts for Host mode. The `host_*` values are for an explicitly available host command, while normal shell commands use the runtime values.

### Agent instructions and personal context

`AgentPersonalContextProvider` renders its instruction template with the object returned by its model factory. A blank template returns an empty context without calling that factory.

The configured Oscova host supplies:

- `agent_name`
- `current_date`
- `current_time`
- `user_name`
- `user_nickname`
- `user_username`
- `user_email`
- `user_timezone`
- `user_context`

The agent editor intentionally does not offer `current_time`. A minute-changing value in standing instructions destabilizes local-model prompt-cache prefixes. Use an allowed date/time tool for on-demand clock access.

`user_context` contains file-backed personal context only for a shared agent. The instruction must reference `{{ user_context }}` for that value to appear.

The `ISharedAgentPersonalContextRepository` contract keeps persistence independent from the host's root topology and authorization. `SharedAgentPersonalContextRepository` encrypts `agent_id`, `owner_user_id` frontmatter and content as `<slug>/context.md.enc` beneath the exact per-user root supplied by the host. The host supplies the content protector and maximum plaintext and stored text sizes. Access-revocation cleanup waits until the consumer unlocks, and a temporarily locked owner does not count as revoked access.

### Tools and agent tools

`AgentToolProvider` resolves the currently allowed qualified tool names on each invocation. It omits missing, denied, and disallowed egress tools, wraps approval-required tools, and can suppress tools that require an unavailable approval channel.

`ToolSearchProvider` contributes its instructions, `search_tools`, pinned tools, tools the current thread has called, and tools that searches loaded in the current turn. Its allowed and pinned name lists are constructor snapshots. Actual tool resolution uses the current tool catalogue.

`AgentAsToolProvider` snapshots successfully resolved child agents when the provider is constructed, then creates a function for each child and the current parent session. Calls resume the child's named conversation from that parent session and save it only after success. Missing IDs are skipped.

`VisionToolProvider` contributes one `analyze_image` tool and creates its `AnalyzeImageTool` wrapper for each invocation.

### Memory

`MemoryContextProvider` reads the complete long-term entry index and the current-day and previous-day logs on each invocation. It has no provider token cap. Detailed long-term concepts remain available on demand through the memory tools supplied to its constructor. The model's context window still limits the complete request.

If `SystemPrompt` omits `{{ agent_memory }}`, the provider appends the generated `<agent_memory>` block automatically.

## Troubleshoot providers

- **The agent fails during construction:** check provider list entries and duplicate `StateKeys`.
- **The model is never called:** inspect the first provider exception or cancellation. The pre-run chain stops immediately.
- **A later provider cannot see an earlier message:** the default input-message filter exposes only external messages to `ProvideAIContextAsync`.
- **Instructions appear in the wrong order:** provider instructions are appended in configured list order.
- **A tool appears twice:** the framework concatenates provider tools without deduplication.
- **A changed child agent is still callable:** rebuild the parent because `AgentAsToolProvider` snapshots child agents at construction.
- **A changed workspace is missing:** use the environment provider's run-time resolver and rebuild any provider whose constructor captured old grants.
- **Post-run storage does not run after an error:** the default `InvokedCoreAsync` skips `StoreAIContextAsync` on failure.

## Related articles

- [Agent Runtime](references/developer-guide-runtime-agent-runtime.md)
- [Agent as Tool](references/developer-guide-runtime-agent-as-tool.md)
- [Toolsets](references/developer-guide-toolsets-toolsets.md)
- [Memory and Skills](references/developer-guide-skills-memory-and-skills.md)
- [Main Agent Settings](references/user-guide-chat-and-agents-main-agent-settings.md)
