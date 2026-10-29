# MCP servers

An MCP server gives your agents tools from a separate program or service. MCP means Model Context Protocol, and a server is either a local process Oscova starts (STDIO) or an HTTP service it connects to. Open **Settings > Agents & AI > MCP Servers** to add one.



## Add a server

1. Select **Add server** and choose the transport, **HTTP** or **STDIO**.
2. Fill in the fields for that transport. **Save server** stays disabled until the required fields are present.
3. Choose the **Network use** declaration that matches the server.
4. Turn on **Enabled** and save. Oscova stores the server and tries to connect immediately.

For **HTTP**, enter:

- **Name**, the server label and tool-name prefix.
- **Endpoint**, the server URL.
- Optional **Headers** as `key=value` lines. Every row needs a name, and blank placeholder values must be filled before enabling.

For **STDIO**, enter:

- **Name**, the server label and tool-name prefix.
- **Command**, the executable to start.
- Optional **Arguments**, one per line. Spaces within a line stay in that argument, and empty lines are ignored. Enter paths with spaces on one line without shell quotes.
- Optional **Working Directory**.
- Optional **Environment Variables** as `KEY=VALUE` literals or bare `KEY` passthrough markers. Every row needs a key, and an enabled connection needs nonblank literal values.

Switching transport clears fields that do not belong to the new transport, such as command, arguments, working directory and environment variables when you move from STDIO to HTTP.

### Choose the device for a STDIO program

- The operator's default is **Server**. Regular users configure local programs on their own enrolled devices and pick one under **Device**, listed by saved name with **Online** or **Offline** status. A user with exactly one device gets it selected automatically.
- Install the executable and its prerequisites on the chosen device. Each connection keeps one device and one command, so create separate connections for a Windows executable and an Ubuntu program.
- Saving for an online device checks the connection. Saving for an offline device keeps the configuration and reports that the device is offline.
- Node-local tools are available in projects on that node. Shared central tools work in workspace and project conversations and run centrally. **HTTP** connections always connect from the server.
- A remote service cannot read a laptop file because you pass it a local path. An agent can send file content to a tool that accepts content, under the usual approval and network rules.

### Host process environment

For a **Host process**, Oscova supplies a curated set of startup variables, such as `PATH` and standard system directories, plus your rows. On Windows, this includes `ProgramData` when present, so Docker MCP Gateway does not need a manual entry for it. An explicit row overrides the inherited value.

- `KEY=VALUE` stores and supplies that literal.
- Bare `KEY` reads the current Oscova Host value when the server launches, and omits the variable when it is absent. Only the marker is saved and displayed, never the resolved value.

Docker-sandbox environment handling is separate.

### Declare network use

Every server has a saved **Network use** declaration. New, imported and registry-created entries default to **Can reach network**.

- **Local only**: its tools do not use the network.
- **Can reach network**: local work may reach the network as a side effect.
- **Internet**: the tools' purpose is external network access.

Offline runs exclude tools from servers declared **Internet**. **Local only** and **Can reach network** servers stay eligible, but HTTP requests to public endpoints are still blocked during an offline run, so choose a loopback or LAN endpoint for offline HTTP use. Oscova trusts the declaration for STDIO filtering and does not inspect the process's own network activity.

### Share a server

The operator can turn on **Share with users** to offer a central connection to everyone. Sharing does not install programs on other computers or expose the operator's configuration fields.

## Connect your Avatar

1. Run `oscova avatar mcp start` on the same machine as the Oscova host.
2. Add an **HTTP** server with **Endpoint** `http://127.0.0.1:11319/mcp`. No authentication header is required.
3. Keep **Network use** on **Can reach network**, because the service can control a LAN Engine and queue audio URLs.

The service shares Avatar pairing with the web interface when both use the same app-data directory. It does not start the Engine or replace the built-in Avatar tools. See [Avatar MCP server](references/avatar-engine-mcp-server.md) for the localhost-only trust boundary.

## Find or import servers

**Find online** searches the public MCP Registry and opens the chosen server in the editor before saving. Any signed-in user can search, and the registry call itself is unauthenticated. Treat results as discovery metadata, not a trust endorsement: review the publisher links, command or endpoint, arguments, requested variables, headers and execution environment before saving. A manual-only registry option cannot be installed from the picker.

**Import JSON** accepts common MCP config JSON from VS Code, Cursor or Claude Desktop:

- A wrapper object with `servers` or `mcpServers`, or a bare server map such as `{ "server-name": { "command": "npx", "args": [] } }`.
- STDIO entries read `command`, `args` or `arguments`, `cwd` or `workingDirectory`, and `env`. HTTP entries read `url` or `endpoint` plus optional `headers`.
- Entries without a command, URL or endpoint are ignored because their transport is unknown.

Imported servers are appended with new IDs and never merged with existing rows. Regular users review each imported STDIO server and select a **Device** before it is saved. Review names, network use, arguments, environment variables and headers before relying on imported tools. Saving fails while required placeholder values are blank, and a success message confirms entries were saved, not that every server connected.

An agent with registry tools enabled can also search and install registry entries. Installation needs your approval, including the proposed **Network use**. If a runtime or required value is missing, Oscova saves the server disabled so you can finish it in Settings, and filling the values and saving turns it on. A successful live install assigns the discovered tools to the requesting agent.

## Manage existing servers

The table shows each server's name, ID, transport, connection summary and option counts. **Configured** means only that the minimum fields are present, not that the server is healthy. **Incomplete** means a required name, endpoint or command is missing. The add, edit and reconnect actions report live connection results.

Row actions:

- Select the row for details.
- Edit and reconnect one server with the pencil button.
- Duplicate a server with a new ID.
- Delete a server and disconnect its tools.
- **Reconnect all** restarts every configured server and reports how many could not reconnect.

What to expect:

- Add and edit save the server separately from the connection result. If connection fails, the server stays in the list with a warning and the failure reason.
- Tool changes synchronize after add, edit, delete and reconnect without a restart. Agents may still need their tool governance settings updated before they can call new tools.
- A connected STDIO process that exits loses its tools until it reconnects.

### Where STDIO servers run

- **Host process** starts the command with the Oscova host's permissions. Trust it as you would any locally installed program.
- **Docker sandbox** starts the program inside the sandbox image on its configured device. Central connections mount the user's workspaces read/write under `/workspace/<name>`. Node project connections mount the project at `/project`.

HTTP servers are remote endpoints without an execution environment. A STDIO process can still make its own network requests, and for either transport the saved **Network use** declaration controls offline filtering.

### Authentication and secrets

If an HTTP server needs authentication, add headers in the editor or use the server's own credential mechanism. MCP headers and literal environment values are stored in your MCP settings and shown in full in the editor and details dialog, not in the protected credential store. Do not put a sensitive literal there unless that visibility is acceptable. Prefer a bare Host-process key when the secret already exists in Oscova's environment.

## Troubleshooting

- **Chat reports an MCP startup failure:** check the named connection on its execution device and reconnect it. If Docker is missing, install it on that device or select native execution. Agents without tools from that connection continue.
- **HTTP server stays unavailable:** confirm the endpoint is an absolute URL, reachable from the Oscova host, with the required headers.
- **STDIO server does not start:** confirm the command is on `PATH`, or use a full path the host process can execute.
- **Arguments look wrong:** the editor splits arguments on spaces. Use a wrapper script for complex shell quoting.
- **Imported JSON finds no servers:** confirm the JSON contains `servers`, `mcpServers` or a bare server map with command or URL fields.
- **Save fails after registry import:** fill every blank required header or literal value, or replace an environment placeholder with a bare key.
- **A row says Configured but tools are missing:** **Configured** checks configuration only. Reconnect, then check the failure message, server logs and tool governance settings.
- **A duplicate tool-name error appears:** rename the MCP server or conflicting tool so its qualified name is unique. Oscova rejects collisions with built-in, plugin and other MCP tools instead of replacing an existing tool or its approval policy.

## Related articles

- [Tool governance](references/user-guide-concepts-tool-governance.md)
- [Main agent settings](references/user-guide-chat-and-agents-main-agent-settings.md)
- [Credentials](references/user-guide-settings-and-admin-credentials.md)
- [Execution](references/user-guide-settings-and-admin-execution.md)
