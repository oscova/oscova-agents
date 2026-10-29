# Toolsets

Oscova exposes callable operations as `AIFunction` instances in one synchronized tool catalogue. Built-in toolsets, plugin toolsets, and connected MCP servers all contribute to that catalogue, but a tool reaches a model only when the agent's allowed-tool and governance settings permit it.



> **Note:**
> A **toolset** is a class whose marked public methods can be reflected into tools. A **qualified tool name** is the stable wire name used by agent definitions and policies, such as `file__read`.

## Know the three tool sources

| Source | Discovery and registration | Qualified-name shape | Lifetime |
| --- | --- | --- | --- |
| Built-in toolset | The runtime synchronizes an `IAgentToolset` through `ToolsetRegistration`. | `<owner>__<tool>` | The registered instance backs its reflected functions until the catalogue is synchronized again. |
| Plugin toolset | An enabled plugin registers a user-scoped factory with `AgentPluginBuilder.AddToolset`. | `<plugin-id>__<owner>__<tool>` | One instance is created while assembling each user scope and can serve multiple runs in that scope. Plugin activation changes require a host restart. |
| MCP tool | A connected server publishes existing `AIFunction` instances and their source metadata. | `<server-name>__<tool>` | Tools remain available while the server is connected. A disconnected server or exited STDIO process loses its tools. Reconnect operations update the catalogue live. |

`IAgentOrchestrator.SyncTools` is the public runtime synchronization boundary. It accepts reflected `ToolsetRegistration` entries, existing external `AIFunction` instances, and `ExternalToolMetadata`. Plugin authors normally use `AddToolset`. Users connect MCP servers through Settings rather than calling this boundary directly.

## Create a reflected toolset

Implement the marker interface `IAgentToolset`. Mark intended public instance and static methods with `[AgentTool]`. Unmarked helpers, inherited methods, and property or event accessors do not become tools.

```csharp
using System.ComponentModel;
using Oscova.Agents;

public sealed class GreetingToolset : IAgentToolset
{
    [AgentTool]
    [DisplayName("say_hello")]
    [Description("Returns a greeting for a name.")]
    public static string SayHello(
        [Description("Name to greet.")] string name)
    {
        return string.IsNullOrWhiteSpace(name)
            ? "A name is required."
            : $"Hello, {name}.";
    }
}
```

The reflected function has the qualified name `greeting__say_hello`. Reflection applies this contract:

- `[DisplayName]` supplies the base tool name. Otherwise the .NET method name is used.
- `[AgentTool]` marks a public method for tool registration.
- `[Description]` supplies tool and parameter descriptions shown to the model.
- Optional .NET parameters use their declared default when the model omits them.
- A `CancellationToken` parameter is supplied by the invocation runtime and omitted from the model-visible schema.
- `[ToolPolicy]` sets the default approval behavior. Without it, the default is `ToolApprovalMode.Default`.
- `[ToolNetworkScope]` classifies network use. Without it, the default is `NetworkScope.None`.
- `[WorkspacePath]` marks a path parameter for containment checks. Its default access is `WorkspacePathAccess.Read`. Mutating paths must specify `Write`.

Keep helpers private or move shared work into a normal service injected into the toolset. Public helper methods without `[AgentTool]` stay unavailable to the model.

The built-in `WaitToolset` is a minimal example of accepting and passing the invocation token:

```csharp
[AgentTool]
[DisplayName("sleep")]
[Description("Pauses the current agent run for a bounded duration when external work has no status handle, then returns so the agent can check again.")]
public static async Task<string> WaitAsync(
    [Description("Seconds to wait. Values below 1 use 1; values above 300 use 300.")]
    int durationSeconds,
    [Description("Short reason for the pause and what will be checked afterward.")]
    string reason,
    CancellationToken cancellationToken = default)
{
    if (string.IsNullOrWhiteSpace(reason))
    {
        throw new ToolFailureException("A reason for waiting is required.");
    }

    durationSeconds = Math.Clamp(durationSeconds, 1, 300);
    await Task.Delay(TimeSpan.FromSeconds(durationSeconds), cancellationToken).ConfigureAwait(false);
    return $"Waited {durationSeconds} seconds. Recheck the external state before continuing.";
}
```

The runtime passes the same token through reflected and MCP tool invocation. Honor it in downstream I/O so stopping a run also stops the work.

## Compose an approval-gated host shell

`HostShellToolset` exposes one foreground tool, `host_shell__exec`, for a harness that needs an explicit host command beside a sandboxed normal shell. Supply a factory that creates and initializes a `ShellExecutor` for the current run:

```csharp
var hostShell = new HostShellToolset(createHostExecutorAsync);
```

The tool creates one executor per call and disposes it when the call completes, times out, or is cancelled. It declares `ToolApprovalMode.RequireApproval` and `NetworkScope.Incidental`. The surrounding harness still decides when the tool enters its catalogue and which run types may invoke it.

Do not treat approval as operating-system elevation. The executor runs with the host process user's permissions unless the harness provides a separately secured executor. Keep unattended-run exclusion, offline enforcement, workspace selection, and any native sandbox policy in the harness that creates the executor.

## Register plugin toolsets

Register a plugin toolset factory from `IAgentPlugin.Configure`:

```csharp
public void Configure(AgentPluginBuilder builder)
{
    builder.AddToolset(_ => new GreetingToolset());
}
```

Use the supplied `AgentPluginContext` when the toolset needs the current user's credentials, HTTP client, logger, or time zone. Do not store user or run state in static fields. See [Plugin Builder](references/developer-guide-plugins-plugin-builder.md) for the complete factory and failure contract.

## Follow the naming contract

`ToolNaming` normalizes each qualifier and tool segment before joining them with `__`:

- letters are lowercased.
- letters, digits, `_`, and `-` are preserved.
- each run of other characters becomes `_`.
- `/`, `\`, and `__` split qualifier segments.
- empty segments are discarded.
- names longer than 64 characters are truncated and end with `__` plus the first eight lowercase hexadecimal characters of a SHA-256 hash.

The toolset owner uses a nonblank class-level `[DisplayName]` when present. Otherwise it uses the class name with an exact trailing `Toolset` suffix removed. The selected name is then normalized. For example, `[DisplayName("Host Shell")]` on `HostShellToolset` produces the owner `host_shell`. Use `ToolNaming.GetToolsetOwner(typeof(YourToolset))` to derive that segment.

| Input | Qualified name |
| --- | --- |
| Built-in class `CodingToolset`, class display name `File`, method display name `read` | `file__read` |
| Plugin ID `acme.search`, class `GreetingToolset`, display name `say_hello` | `acme_search__greeting__say_hello` |
| MCP server `GitHub`, remote tool `list_issues` | `github__list_issues` |

Agent allowed-tool lists and policies use the qualified name, not the .NET method name or an MCP server's unqualified tool name. Catalogue lookup is case-insensitive, while the originally published function name remains unchanged.

Qualified names must be unique across all sources. Publishing `GitHub` and `github` tools with the same normalized remote name is therefore a collision. Synchronization rejects a duplicate instead of silently replacing a function or its governance metadata, and a failed complete synchronization does not publish a partial replacement. Rename the tool, toolset, plugin ID, or MCP server so the qualified names differ.

## Understand metadata and filtering

Agent tool exposure applies the selected tools, saved approval policy and Offline mode. Encrypted file tools are available to any agent when explicitly selected and permitted by its folder grants.

Each catalogue entry retains its qualified name and effective governance metadata. Reflected tools derive owner, original name, approval mode, and network scope from the toolset and attributes. External functions use `ExternalToolMetadata` for server name, original name, and network scope. MCP descriptions and argument schemas come from the remote server.

MCP tools use the server's saved `NetworkScope`, which defaults to `Incidental` for both HTTP and STDIO connections. A per-tool governance override can change the effective value. Classify tools whose purpose is external network access as **Internet** (`Egress`) so offline tool filtering can hide them. Endpoint transport guards remain separate from tool classification. A STDIO process can still reach the network even when its tools are classified as local work.

Catalogue membership does not grant execution. When building an agent, `AgentToolResolver` starts with the agent's allowed qualified names and then:

1. Omits names that are missing from the current catalogue.
2. Omits `NetworkScope.Egress` tools for local-only or air-gapped agents.
3. Applies the effective tool policy.
4. Omits denied tools, wraps approval-required tools, and exposes allowed tools as callable functions. A surface that cannot request approval omits approval-required tools instead.

Workspace path validation and other invocation middleware still run when the model calls an exposed tool. Visibility, approval, and input validation are separate protections.

## Use tool search mode

With tool search enabled, an agent receives a smaller initial tool list:

- the `search_tools` discovery function.
- pinned tools that are always visible.
- tools the current thread has already called.
- tools that searches loaded earlier in the current turn.

`search_tools` returns at most five lexical matches across qualified names, descriptions, and argument names. Matching tools are added to the current run and stay available until the next user message, including when a tool approval resumes the turn. Pinned tools are excluded from search results because they are already present. The same eligibility and resolver rules still omit missing, denied, or disallowed network tools.

Write distinct display names and concrete descriptions. For example, `search_customer_orders` with a description that names its `customer_id` argument is easier to discover than a generic `search` tool.

## Handle results, cancellation, and errors

Large text results show their beginning, true size or result count, and a path to saved output. Command output shows its beginning and end. Saved output is encrypted at rest and lasts until its chat, incognito conversation or task is deleted. Select an encrypted reader or search tool explicitly to inspect the saved content.

- Throw `ToolFailureException` for invalid arguments and operations the tool could not complete. Write its reason and optional fix for the agent to act on.
- Return empty or domain-specific results normally when the operation itself completed.
- Keep failure reasons and fixes safe for conversation history. Do not include paths, host names, connection strings, credentials, or raw exception text.
- The Oscova host reports deliberate failures as `Failed:` text and summarizes the third matching failure for the same tool and arguments. It preserves approval round trips when they are present. This reporting is host-supplied behavior, not an automatic guarantee of a standalone `AgentOrchestrator`. Tool code does not need its own failure counter.
- Let cancellation propagate. Do not catch `OperationCanceledException` and return success.
- Pass `CancellationToken` to HTTP, storage, process, and MCP operations.
- Log unexpected exceptions at external boundaries, then let them propagate. The harness reports them opaquely, so do not turn them into plausible success text.
- Never rely on model instructions alone for paths, URLs, command text, credentials, IDs, or authorization checks.

Tool instances may serve concurrent runs. Keep per-call data local, make mutable shared state safe, and do not start background work that outlives the public tool or plugin lifecycle.

## Troubleshoot discovery

If a tool does not appear or cannot be called, check these in order:

1. Confirm the source is active: the built-in registration exists, the plugin loaded after restart, or the MCP server is connected.
2. Find the exact qualified name in Settings. Do not guess it from the friendly label.
3. Confirm the agent's allowed-tool list contains that qualified name.
4. Check for a denied policy or a network-scope restriction.
5. In tool search mode, pin the tool or search using terms from its name, description, or argument names.
6. Check load and connection diagnostics for duplicate normalized names, invalid reflected metadata, factory failures, or remote errors.

## Related articles

- [Tool Policies](references/developer-guide-governance-tool-policies.md)
- [Workspace Path Safety](references/developer-guide-governance-workspace-path-safety.md)
- [Plugin Builder](references/developer-guide-plugins-plugin-builder.md)
- [Build Your First Plugin](references/developer-guide-plugins-first-plugin.md)
- [MCP Servers](references/user-guide-connections-mcp-servers.md)
