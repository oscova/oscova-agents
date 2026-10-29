# Privacy

Privacy settings let each signed-in user keep agent runs on local resources. Offline mode blocks public egress for that user's agent runs without changing other users or shutting down the host network.

> **Note:**
> Egress means data leaving the machine or local network for an outside service.

## Turn on offline mode

1. Open **Settings > Accounts & Access > Privacy**.
2. Enable **Keep agent runs offline**.
3. Select **Save Privacy Settings**. **Reset** discards an unsaved draft.



The saved setting applies to later runs and rebuilds cached agents. When offline mode is active, Oscova blocks:

| Surface | Offline behavior |
| --- | --- |
| Model providers | Providers classified as cloud are rejected before credential lookup |
| Tools | Tools classified as **Internet** are not exposed to the agent |
| Host-managed HTTP | Requests to public endpoints are rejected during the run |
| Public HTTP MCP | Public server calls and connection attempts are blocked |
| Embeddings | Cloud-backed memory and Library search and indexing are blocked |
| Speech | Cloud speech recognition and synthesis providers are blocked |
| Shell | Host execution is rejected. Isolated environments, including Docker and Microsoft MXC, deny network access |

Local model providers, local tools, loopback services and LAN endpoints remain available when their own configuration allows them.

> **Important:**
> Offline mode is an agent-run policy, not a host-wide network switch. Settings operations and explicitly enabled external channel transports have their own behavior.

## Sign out an idle browser

**Automatic sign-out** ends this browser profile's session after a period without human activity.

1. Open **Settings > Accounts & Access > Privacy**.
2. Under **Automatic sign-out**, choose **Off**, **5 minutes**, **15 minutes** or **30 minutes**.
3. Select **Save Privacy Settings**.

- Mouse, touch, keyboard, scrolling and submitting a message count as activity. Agent responses, streaming output, text-to-speech, microphone listening, scheduled work and connected-channel traffic do not.
- Open Oscova tabs for the same user in one browser profile share activity and the selected timeout. A different browser profile, device or signed-in user keeps an independent idle clock.
- Automatic sign-out uses ordinary logout. It clears the browser session and discards incognito chats. It does not lock the Host's already-unlocked data key, remove the retained user scope or cancel agent tasks and ordinary detached work.

## Read the live effect

When the draft toggle is on, **Live effect** shows:

- **Internet**: the number of currently known Internet-classified tools disabled by the policy.
- **Cloud providers blocked**: configured model providers classified as cloud.
- **Local network**: a reminder that loopback and LAN endpoints remain available.
- **Redaction**: saved-secret masking is always on.
- **Connected channels**: Discord, Telegram and Slack remain external transports when enabled.

The count and provider list describe the current configuration. They are not a complete list of every possible external action, and they do not include cloud speech or channel traffic.

Saved secret values are masked in agent logs, approval details and captured request data. Redaction cannot be disabled from this page.

## Understand connected channels

Discord, Telegram and Slack connect to their own external servers for polling, gateway or Socket Mode traffic, approvals and replies. Offline mode does not stop those bot connections, but local-only runs do not download attachments from these channels.

- Messages received through a channel run the selected agent under the user's global and agent-level local-only policy. Model, embedding, speech, shell and tool execution can stay local while the channel transport remains external.
- Choose any agent you are authorized to use for Discord, Telegram or Slack. Its conversation is encrypted under your user key, so unlock your Vault Password before using the connection. If the selected agent is deleted or access is removed, the next message uses your main agent.
- Outbound channel replies appear in the run's Egress Audit. Inbound gateway, polling and attachment transport stay outside that ledger because they bring data into Oscova rather than sending run data out.

To disconnect a transport now, select **Stop Bot** in the Discord, Telegram or Slack settings. If it must stay stopped after the next Host restart, also turn off **Connect at startup** and select **Save Settings**.

## Keep one agent local only

Use agent-level **Local only** when one agent must stay local while the user's other agents may use public services.

1. Open **Settings > Agents**.
2. Edit the agent.
3. Enable **Local only**.
4. Save the agent.

Agent-level local-only applies the same provider, tool, embedding, speech and shell restrictions to that agent's runs. Global Offline mode and agent-level **Local only** combine as a ceiling: an agent cannot disable the user's active global policy, and a delegated agent cannot weaken the initiating run's offline posture.

## Encrypted conversations

Encryption protects stored conversations, task runs, memory and private notes for every agent. **Local only** controls whether the run can send data to cloud providers or internet tools. These are independent controls, and both need your Vault Password unlocked before an agent runs.

- Logging out leaves the unlocked key in the running Host so background tasks can continue. Restarting the Host locks it, and Oscova asks for the Vault Password before loading authenticated content.
- After a restart, tasks wait until their owner unlocks the Vault Password. Locked attempts invoke no handler, tool or hook and create no run record. Tasks keep their enabled state and resume through the scheduler after unlocking.
- Task outcome notifications use generic in-app messages without result or error content.

## Protect your files on disk

Your Vault Password encrypts what Oscova stores about you, such as conversations, memory, notes, agents and settings. Files you work with stay as normal files so the shell, your editor and other tools can open them. This covers workspace files, project folders, Library folders and chat attachments.

To protect those files if your computer is lost or stolen, turn on your operating system's disk encryption: BitLocker or Device Encryption on Windows, FileVault on macOS, or LUKS on Linux. To encrypt a single text file instead, create it in **Files** with an `.enc` extension, such as `notes.md.enc`.

## Classify provider endpoints

Oscova classifies model endpoints automatically:

- Loopback, private LAN and link-local addresses are local.
- A hostname is local only when all its resolved addresses are local.
- Public, unresolved or mixed local/public DNS results are cloud.
- Built-in local provider types are local.

For a proxy or gateway that automatic detection classifies incorrectly, edit the provider and set **Endpoint scope**:

- **Auto-detect** uses the provider type and endpoint.
- **Treat as local** allows the provider in local-only runs. Use it only when you control the endpoint and know traffic stays on an approved local or LAN route.
- **Treat as cloud** blocks the provider in local-only runs.

Tool network classification is managed separately under **Settings > Tools**. A tool marked **Can reach network** is not removed by the Internet-tool filter, so classify tools according to their real purpose.

## Troubleshooting

- **No provider is available**: configure a local provider or correct its **Endpoint scope**.
- **A LAN provider is blocked**: confirm every DNS result is local, or choose **Treat as local** after verification.
- **A cloud credential error appears**: confirm the run is using the intended local provider. Cloud providers should be blocked first.
- **An Internet tool is missing**: turn off the applicable local-only policy or use a local alternative.
- **A public HTTP MCP server is unavailable**: public MCP traffic is blocked during an offline run.
- **Memory search or indexing fails**: select a local embedding provider.
- **Speech fails**: select a local speech provider or leave local-only mode.
- **Host shell fails**: select an execution provider that supports offline isolation, such as Docker.
- **Discord, Telegram or Slack still sends traffic**: stop that channel. Offline mode does not disable its transport.

## Related articles

- [Tool governance](references/user-guide-concepts-tool-governance.md)
- [Model providers](references/user-guide-concepts-model-providers.md)
- [Network](references/user-guide-settings-and-admin-network.md)
- [Egress Audit](references/user-guide-monitoring-egress-audit.md)
- [Connected accounts](references/user-guide-connections-connected-accounts.md)
