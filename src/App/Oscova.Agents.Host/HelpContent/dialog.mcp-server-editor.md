# Configure MCP server

Set the connection used to make this server's tools available.

> **In plain terms:** HTTP connects to a service, while STDIO starts a program.

- Enter **Name** and select **Transport**.
- HTTP needs **Endpoint** and optional headers, one `key=value` per line.
- STDIO needs **Command** and may use arguments and a working directory.
- Enter one argument per line. Keep a path containing spaces on one line, without shell quotes.
- Select **Device** for personal STDIO programs and install the program there.
- Regular users must choose an enrolled device for STDIO.
- **Host process** uses that device's application permissions.
- **Docker sandbox** requires Docker on that device.
- Choose **Network use** to match the service's actual access.
- **Environment Variables** accepts saved `KEY=VALUE` entries.
- For Host processes, bare `KEY` reads that variable at launch, omitting missing values.
- **Save server** applies the configuration, while **Cancel** discards edits.
- A server an agent installed with blank values stays off until you fill them and save.

> **Note:** Stored header and literal environment values are visible in server details. They are not protected Credentials entries.

> **Note:** Saving does not prove a successful connection. Check the status and reconnect after correcting errors.

## Related

- [MCP Servers](settings.mcp.md) - return to the parent screen's help.

Containerized STDIO requires an enabled container execution plugin, Docker, on the execution device. Enable exactly one launcher and restart. Missing capability stops the connection without switching to native execution.
