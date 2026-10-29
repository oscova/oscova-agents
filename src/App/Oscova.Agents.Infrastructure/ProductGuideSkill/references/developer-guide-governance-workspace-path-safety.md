# Workspace path safety

Use Oscova's workspace path contract when a tool reads or changes files. The contract gives the agent a stable path vocabulary, translates those paths for host file operations, and authorizes each marked argument against the active run's directory grants.

This contract is part of the reusable `Oscova.Agents` project. The supported extension points are `AllowedDirectory`, `WorkspacePathAttribute`, `WorkspacePathValidationMiddleware`, `PathHelper`, and `IWorkspaceFileAccess`.

## Keep translation and authorization separate

Path translation answers, "Which host path does this agent-facing path identify?" Authorization answers, "May this run read or write that path?" A safe file tool needs both:

1. Mark every file or directory parameter with `[WorkspacePath]` and the correct access mode.
2. Let `WorkspacePathValidationMiddleware` authorize the argument at invocation time.
3. Perform physical file operations through `IWorkspaceFileAccess`.
4. Apply the tool's own existence, format, size, content, and operation-specific checks.

Oscova wires `UseWorkspacePathValidation` into its agent pipeline. The middleware calls the directory resolver for each invocation, finds marked parameters on the underlying tool method, and authorizes nonblank values before the tool runs. If you build a `ChatClientBuilder` outside Oscova's pipeline, you must call `UseWorkspacePathValidation` yourself.

> **Warning:**
> The middleware denies nonblank `[WorkspacePath]` arguments when the allowed-directory list is empty. A standalone host must supply a current resolver for any permitted file access. Calling a tool method directly still bypasses invocation middleware.

## Describe resolved grants

`AllowedDirectory` describes one directory available to workspace-aware tools:

| Member | Meaning |
| --- | --- |
| `Value` | Resolved host directory path |
| `Label` | Human-readable name shown in workspace context |
| `Kind` | Source classification. Path resolution operates on `LocalPath` entries |
| `AccessMode` | `ReadOnly` or `ReadWrite` |
| `AgentPath` | Optional canonical file-tool alias and container mount target |

Resolve user-relative directory definitions to trusted host paths before adding them to the active list. Use a live `Func<IReadOnlyList<AllowedDirectory>>` where configuration can change, so prompt context, middleware, and tool resolution use the same grants.

The Oscova host combines the file sources available to the run. These can include selected managed workspaces, the agent's private directory, additional folder grants, and temporary run directories such as chat attachments. Access metadata remains part of each grant. For example, the Library workspace and chat attachments are read-only, while the agent's private folder is read-write.

## Use the agent-facing path vocabulary

File tools accept stable logical roots in every execution mode:

| File source | Agent-facing root |
| --- | --- |
| Named managed workspace | `/workspace/<name>` |
| Agent private folder | `/self` |
| Additional or run-scoped directory | `/mnt/<label>` |

The agent private folder used the root `/private` before `/self`. `private` stays a reserved root, so a relative `private/...` path or an ungranted `/private/...` path is refused, while a native path inside a granted folder under the system `/private` directory on macOS still resolves. Chat threads keep their stored paths: opening a thread artifact maps the retired root to `/self` once, in `ArtifactFileAccessService`. A Markdown link to a native path under `/private` is read the same way, as an agent private folder path. New links should use the canonical aliases.

Workspace names and labels are converted to lowercase mount names. Letters and digits are retained, runs of other characters become one hyphen, leading and trailing hyphens are removed, and an empty result becomes `workspace`. Reject duplicate effective mount paths before starting the run.

Assistant assigns the same `AgentPath` in every execution mode. The reusable framework also supports native-only grants without an alias, for which reverse projection returns the native path.

| Mode | `Value` | `AgentPath` | Shell address |
| --- | --- | --- | --- |
| Host on Linux | `/home/user/workspaces/research` | `/workspace/research` | `/home/user/workspaces/research` |
| Docker | `/home/user/workspaces/research` | `/workspace/research` | `/workspace/research` |

`RuntimeEnvironmentInfo.UsesHostPaths` identifies the shell namespace. Prompts and file-tool output use `PathHelper.ResolveToAgentPath` for canonical aliases in every mode. Directory rows include complete paths across pagination. Shell `workdir` resolves an existing granted directory on the executing device. Commands then use relative paths or the root environment variables shown in the environment prompt. Native variables contain physical paths, while mapped variables contain mounted paths. Server roots described beside a node project require server file routing and are excluded from node shell variables.

In every mode, ordinary relative paths resolve against the operation's writable working directory. In a project chat, the selected project is the default file-tool base. Without a project, the selected `default` workspace wins when it is writable, otherwise the first writable local grant is used. The old `/workspace` working-directory shorthand is not supported. Use a bare relative path or a full named alias.

Built-in file and directory tools route granted workspace, private and attachment aliases to the server, and `/project` to the selected project's device. Their optional `environment` selects a different base for relative or native paths, and must agree with an alias's owner when supplied. Native paths do not identify a device automatically. Filename globs search their selected directory, and an empty result identifies that directory without expanding the search to other roots.

Logical and mounted roots are matched exactly and case-sensitively. A leading slash is optional for file tools. Redundant separators and `.` segments are normalized before matching. A `..` segment inside a recognized logical route is rejected without falling back to the working directory. Unknown `workspace`, `private`, and `mnt` roots also fail instead of becoming relative subfolders.

Host containment follows the operating system's path behavior through `Path.GetRelativePath`, so Windows host paths compare case-insensitively while Linux host paths remain case-sensitive. Fully qualified native paths remain available when their physical directory is granted.

> **Important:**
> Never show the agent a path that cannot be resolved through `PathHelper.TryResolveToHostPath` or, in sandbox mode, is not mounted at that same path.

File, encrypted-file, directory, search, and PDF tools perform their file operations in the host process through one `IWorkspaceFileAccess` instance. In Docker execution, the shell receives bind mounts at the same agent-facing roots, with each mount set to read-only or read-write from its grant. Both surfaces therefore address the same bytes even though they execute in different processes.

Host placement alone does not make these surfaces equivalent. File tools remain bounded by path translation and workspace middleware. A host shell executes arbitrary commands with Oscova's operating-system account permissions, so its file and network reach is not contained by `AllowedDirectory` grants.

When a file operation returns a path to the model, project its authorized physical address through `PathHelper.ResolveToAgentPath`. Search, Library citations and indexing status, directory rows, file confirmations and offloaded results follow this contract. Artifact references are reauthorized against the producing agent's current grants. Native and alias file inputs remain accepted. Short aliases are not operating-system mounts in native execution.

## Understand the authorization checks

`PathHelper.ResolveAuthorizedHostPath` performs the checks used by workspace middleware:

1. Replace slash direction as needed and translate a matching agent-facing root.
2. Resolve relative paths against the working directory and normalize the result with `Path.GetFullPath`.
3. Require the normalized path to remain inside a local allowed directory.
4. Select the most specific containing grant when grants overlap.
5. Prefer `ReadOnly` when equal directory roots carry conflicting access modes.
6. Reject existing symlink or reparse-point segments beneath the selected root.
7. Require `ReadWrite` when the parameter declares write access.

Before context rendering or file IO, the host rejects an allowed-directory set when a rooted logical alias overlaps a different native grant. The check compares both containment directions. An overlap is allowed only when routing through the alias reaches the same physical location. On Linux, `/self` cannot identify agent private files while a separate grant uses a native `/self` folder. The user must remove that grant or move it to an unambiguous physical location.

Normalization blocks traversal such as `../../outside.txt` when it escapes the grant. Textual prefix checks are not sufficient because separators, `..` segments, rooted paths, and platform-specific path behavior can change the resolved location.

Missing target segments are allowed. This is necessary for tools that create a new file or directory. Existing inaccessible segments are treated conservatively as unsafe because their reparse-point status cannot be verified.

The grant root itself is trusted host configuration. The symlink check applies to path segments beneath that root.

## Keep physical operations inside the grant

Path authorization alone cannot safely separate checking a path from opening it. A workspace process could replace an approved descendant directory with a link during that interval. `IWorkspaceFileAccess` closes that gap for host file tools by walking from a held granted-root handle and opening every descendant relative to its held parent.

The implementation rejects link traversal, keeps the destination parent open while publishing an atomic write, and enumerates directory entries without following links. A returned read stream owns its opened file handle, so later path replacement cannot redirect that stream. Writes still honor the most specific grant, including a read-only grant nested inside a writable one.

Configured top-level grant roots and their ungranted ancestors remain trusted host configuration. Missing grant roots fail instead of being created. The boundary does not constrain a host process that can modify trusted configuration or access files outside workspace tools.

## Mark and resolve a tool path

This example follows the same pattern as the built-in read tools:

```csharp
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using Oscova.Agents;
using Oscova.Agents.Toolsets;
using Oscova.Agents.Workspace;

public sealed class NotesToolset(
    Func<IReadOnlyList<AllowedDirectory>> workspaceResolver) : IAgentToolset
{
    private readonly IWorkspaceFileAccess _files = new WorkspaceFileAccess(workspaceResolver);

    [AgentTool]
    [DisplayName("read_note")]
    [Description("Reads a text note from an approved workspace.")]
    public string ReadNote(
        [WorkspacePath(Access = WorkspacePathAccess.Read)]
        string path)
    {
        using Stream stream = _files.OpenRead(path);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
```

The middleware authorizes `path` before `ReadNote` runs. `WorkspaceFileAccess` snapshots the same live resolver for the operation, repeats authorization, and returns a stream already opened under physical containment. For a write, use `WorkspacePathAccess.Write` and call `IWorkspaceFileAccess.Write`.

If your tool embeds more paths inside another argument, such as patch text or a manifest, middleware cannot discover those nested values. Parse them, normalize them, and authorize every target explicitly with `ResolveAuthorizedHostPath` before performing IO.

`CodingToolset.ApplyPatch` resolves `base_directory` through the same logical aliases. Paths inside the patch remain relative to that selected base directory. To patch another workspace, select its alias as `base_directory` and keep patch entries relative to it.

## Handle failures

Workspace failures are surfaced before the tool body runs:

| Condition | Result |
| --- | --- |
| Path cannot be resolved or is outside every grant | `UnauthorizedAccessException` from authorization |
| Existing symlink or reparse-point hop | `UnauthorizedAccessException` |
| Descendant directory replaced during an operation | The operation fails or continues on its already opened file or directory, without following the replacement |
| Write requested in a read-only grant | `UnauthorizedAccessException` |
| Invalid platform path syntax | The normal path-format exception from .NET |
| Blank or omitted marked argument | Middleware skips it. The tool must enforce whether it is required |
| Missing file or directory inside a grant | Authorization succeeds. The tool decides whether absence is valid |

Keep errors actionable, but report the agent-facing input rather than a newly discovered host path. Use `PathHelper.SanitizeForLog` before placing an untrusted path in logs so control characters cannot inject log lines.

## Treat MCP execution as a separate boundary

`[WorkspacePath]` protects tool methods invoked through Oscova's function middleware. It does not inspect filesystem behavior inside an MCP server or another external process.

STDIO MCP servers can run in either environment:

- **Host process** is the default and runs with Oscova's host permissions. Workspace middleware does not confine the server's internal file access.
- **Docker sandbox** runs the server in the shared sandbox image and mounts the current user's managed workspaces read-write under `/workspace/<name>`. These mounts are server-level access, not the active agent's `AllowedDirectory` list.

Choose the MCP execution environment and server configuration as an independent trust decision. HTTP MCP servers access files only through whatever capabilities their remote endpoint provides.

## API reference

- AllowedDirectory
- WorkspacePathAttribute
- WorkspacePathValidationMiddleware
- PathHelper
- IWorkspaceFileAccess
- WorkspaceFileEntry

## Related articles

- [Toolsets](references/developer-guide-toolsets-toolsets.md)
- [Tool Policies](references/developer-guide-governance-tool-policies.md)
- [Workspaces](references/user-guide-concepts-workspaces.md)
- [MCP Servers](references/user-guide-connections-mcp-servers.md)
