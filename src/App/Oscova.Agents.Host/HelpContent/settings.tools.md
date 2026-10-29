# Tools

Tool identifiers appear beneath tool names. On narrow screens, each row becomes a labelled group with the same policy controls.

Tools lists callable capabilities and lets you set per-user network classifications and 24-hour call limits.

> **In plain terms:** this screen governs known tools. Select tools inside an agent or task scope.

## Find and govern a tool

- Search by tool name, server, or qualified name.
- Filter the catalogue by server.
- **Internet** shows the effective network classification.
- **Blocked** means global offline mode currently blocks that Internet tool.
- **Policy** shows default or custom governance and current quota use.
- Open a row's policy action.
- Use **Auto-approve** only when the tool may run unattended with the agent's full configured access.
- Saving affects future runs but does not select the tool for an agent.

> **Note:** offline mode blocks Internet tools. Network classification does not sandbox a host process.

> **Note:** Auto-approved shell commands can change the machine running Oscova when host execution is enabled.

> **Note:** File tools fail when a granted folder is inaccessible or replaced. Check the folder under Workspace Access.

## Resolve a blocked call

- Check the agent or task selected the tool.
- Check global Offline mode and agent-level **Local only**.
- Check agent approval for **Deny**.
- Check quota use and the reported UTC reset time.

## Related

- [Agents](settings.agents.md) - select tools for custom agents.
- [Privacy](settings.privacy.md) - control offline runs.
- [Tool policy](dialog.tool-governance.md) - help for this dialog.
