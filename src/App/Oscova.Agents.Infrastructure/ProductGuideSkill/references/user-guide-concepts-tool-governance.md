# Tool governance

Tool governance decides which tools an agent can receive and which checks must pass before a call runs. Four separate controls apply: selection, network classification, approval and call limits.



## Inspect the Tools catalogue

Open **Settings > Tools** to review the built-in, plugin and connected MCP tools Oscova knows about. The catalogue is for inspection and per-user policy. It does not select tools for an agent: use **Main Agent**, **Agents** or a custom task run scope for that.

Search by visible name, server or qualified name, and use the server filter to narrow the source. The table shows:

- **Tool**: visible name and description.
- **Qualified Name**: the exact identifier saved in agent and policy settings.
- **Server**: the tool's source.
- **Internet**: the effective network classification.
- **Policy**: default or custom governance, including quota usage.

The **Blocked** badge means the current global offline setting prevents that Internet-classified tool from being used. Agent-specific local-only blocks also apply during that agent's runs.

## Control selection and discovery

Tool selection sets the maximum set available to an agent. If a tool is not selected, the agent cannot call it.

Tool discovery controls when selected tools enter the model's context:

- With discovery off, all permitted selected tools are visible at run start.
- With discovery on, pinned tools are visible at run start. Other selected tools become visible after the agent finds them through tool search.
- Tool search matches qualified names, descriptions and argument names, and returns up to five matches. It excludes tools blocked by local-only policy, denial or delegated-run approval rules.

Memory and private notes are encrypted for every agent. Daily Memory and Reflection can read encrypted conversations after the vault is unlocked. Conversation recall uses encrypted indexes and requires Enable Memory and a working embedding provider.

An agent exposed as a tool can be selected by another agent. The called agent keeps its own file grants and local-only policy. In web conversations, delegated approval requests return to the parent chat and name the child that executes the tool. Session grants remain specific to that child, and other channels keep their own approval rules.

## Classify network behavior

Each tool has an effective network scope:

- **Local only** means no network access is expected.
- **Can reach network** means the tool's main job is local but it may use the network.
- **Internet** means outbound network access is central to the tool.

Open **Tool policy** from a row to keep **Use declared scope** or override the scope. Saving changes later runs and refreshes cached agent definitions. An override never selects a tool or bypasses an agent denial, so use the least permissive accurate classification.

Global Offline mode or agent-level **Local only** blocks **Internet** tools before the model receives them. It also blocks public host-managed HTTP calls and cloud providers. Local and LAN endpoints remain available.

> **Important:**
> **Can reach network** tools are not removed by local-only tool filtering. Use **Internet** for tools whose purpose is external access.

Network scope is classification metadata, not an operating-system firewall. Classify a network-capable STDIO MCP tool as **Internet** when outbound access is part of its purpose. A host-run STDIO process has host permissions, and a Docker-run server has the mounts and network available to that container.

## Set approval behavior

Each selected tool has an agent or task-scope approval mode:

- **Default** uses the tool's declared default.
- **Require approval** asks before an interactive call proceeds.
- **Auto-approve** exposes the call without a prompt.
- **Deny** removes the tool for that scope.

Ordinary and encrypted file **Write** and **Edit** tools default to **Auto-approve**, including overwrites within writable folders. Choose **Require approval** or **Deny** to restrict them for an agent or task. Shell commands and file patches still require approval by default. Folder permissions and path validation always apply.

Oscova checks a call in this order:

1. The tool must exist and be selected.
2. Offline or local-only network policy must allow it.
3. The agent or task policy may require approval, auto-approve or deny it.
4. Discovery decides when a permitted tool becomes visible.
5. The run handles any required approval.
6. Quotas, hooks, path containment and tool validation still apply.

Encryption of stored conversations does not add a separate agent category. Auto-approve affects only approval-required calls: it cannot restore an unselected, denied or offline-blocked tool and cannot bypass later execution checks.

Web chats also offer **Allow once**, **Allow for this session** and **Deny**, with current grants and revocation in **Session permissions**. Temporary grants survive refresh but expire when the server session ends. Saved approval labels never restore grants. See [Approve tools](references/user-guide-chat-and-agents-main-agent-chat.md) for the saved composer presets and operator-only **Full access**, including execution on connected project devices.

> **Caution:**
> Host execution is the default for shell commands. Auto-approved host shell commands run without a human prompt and can change the machine running Oscova with its operating-system account permissions. Workspace file grants do not contain arbitrary host shell code.

Interactive web, Discord and Telegram runs route approval requests to their active channel. Background tasks cannot wait for a person, so a task must choose:

- **Auto-reject approval-required tools**, the safer default.
- **Auto-approve approval-required tools**, for a reviewed unattended workflow.

## Limit calls for a user

1. Open **Tool policy** for the tool.
2. Enable **Limit calls** and enter a maximum from 1 to 1,000,000.
3. Save.

The limit applies to that qualified tool for the signed-in user across agent run surfaces. The fixed 24-hour window starts with the first counted call, and the catalogue shows usage as `used / maximum`. When the budget is exhausted, Oscova stops the run before invoking the tool and reports the UTC reset time.

Pre-tool hooks can also deny a call, and file tools can reject unsafe or read-only paths. Removing both the network override and the call limit returns the row to its default catalogue policy.

## Troubleshoot an unavailable tool

- Confirm the tool still appears in **Settings > Tools** after connecting or reconnecting its source.
- Confirm it is selected in the current agent or task run scope.
- If discovery is enabled, pin a frequently needed tool or let the agent search for it.
- Check for **Blocked**, **Deny** or **Require approval** settings.
- In a delegated web run, return to the parent chat for the approval card.
- Check quota usage and the reported reset time.
- Review global Offline mode and the agent's **Local only** setting.
- Review MCP server execution permissions and connectivity separately from tool selection.

## Related articles

- [Agents, tools and skills](references/user-guide-concepts-agents-tools-skills.md)
- [Included tools](references/user-guide-concepts-included-tools.md)
- [MCP servers](references/user-guide-connections-mcp-servers.md)
- [Privacy](references/user-guide-settings-and-admin-privacy.md)
- [Agent tasks](references/user-guide-automation-agent-tasks.md)
- [Workspaces](references/user-guide-concepts-workspaces.md)
