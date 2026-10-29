# MCP Servers


The server list adapts to the available width, with labelled groups on narrow screens.

Connect external programs or services whose tools your agents can use.

> **In plain terms:** an MCP server gives agents extra actions.

## Add a server

- Open **Settings**, choose **Agents & AI**, then select **MCP Servers**.
- Click **Find online** to review an MCP Registry result.
- Registry listings aid discovery but do not guarantee trust.
- Click **Import JSON** to import a common MCP configuration.
- Click **Add server** to configure HTTP or STDIO manually.

> **Note:** Save success does not prove connection. Failed and incomplete servers remain saved for correction.

- New server tools start unselected. Select them under **Allowed Tools** in each agent's settings before an agent can use them.

- Air-gapped runs omit **Internet** tools. Other declarations remain eligible, but public HTTP requests are still blocked during the run.



## Manage connections

- **Enabled** controls availability. The operator's **Share with users** offers a central connection to everyone.
- Personal STDIO programs use one **Device** selector with your enrolled device names and Online/Offline status. Install the program on that device. Offline configurations can be saved and checked when the device reconnects.
- Node-local tools work in projects on their assigned node. Shared central tools stay on the server during workspace and project chats.
- HTTP connects from the server without a Device selector. File paths refer to the machine running the MCP service. Sharing never installs a program elsewhere or grants a remote service access to laptop files.

> **Note:** Run `oscova avatar mcp start` to expose Avatar tools. Add its localhost HTTP endpoint to connect.

- Edit reconnects one server, while **Reconnect all** retries every server.
- Changes apply live without restarting Oscova.
- A tool-name collision blocks connection instead of replacing another tool.
- A failed connection does not block agents that do not use its tools. If an agent needs that connection, follow the chat's recovery message and reconnect it here.

Containerized STDIO requires an enabled container execution plugin, Docker, on the execution device. Enable exactly one launcher and restart. Missing capability stops the connection without switching to native execution.

## Related

- [Tools](settings.tools.md) - review available tools.
- [Import MCP configuration](dialog.mcp-import.md) - help for this dialog.
- [Find MCP servers online](dialog.mcp-registry-browser.md) - help for this dialog.
- [Configure MCP server](dialog.mcp-server-editor.md) - help for this dialog.
- [MCP server details](dialog.mcp-server-details.md) - help for this dialog.