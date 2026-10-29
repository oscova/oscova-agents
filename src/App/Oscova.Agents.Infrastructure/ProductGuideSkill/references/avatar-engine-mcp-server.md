# Avatar MCP server

Let another assistant control your Avatar through Model Context Protocol (MCP), a standard way for assistants to discover and call tools. The existing `oscova` tool runs this server independently. Direct Engine controls work without the Oscova web host. Text speech through `avatar_speak` additionally requires the running Assistant host and its operator login.

## Start and connect

1. Install the [Oscova command-line tool](references/user-guide-getting-started-command-line.md).
2. Run `oscova avatar mcp start` on the machine running your MCP client.
3. Add an HTTP (Streamable HTTP) server to that client with endpoint `http://127.0.0.1:11319/mcp`.
4. Start the Avatar Engine when you want to use its controls. On a Windows renderer, use `oscova avatar start`.

In Oscova's [MCP Servers](references/user-guide-connections-mcp-servers.md) editor, choose **HTTP** and enter that endpoint. Leave authentication headers empty. **Can reach network** is an appropriate declaration because tools can contact a LAN Avatar and ask the Engine to download audio.

MCP starts detached and remains running when the terminal closes. It lists tools even while the Engine is offline. Starting MCP does not install or start Unreal or the Oscova web host.

```text
oscova avatar mcp start
oscova avatar mcp status
oscova avatar mcp stop
```

Use `start --port 11320` if the default MCP port is occupied, then use the corresponding endpoint in your client. Use `start --avatar-url http://192.168.1.25:11318` to save a different downstream target after successful startup. Omitting the URL reuses Avatar Settings. A running service must be stopped before changing these startup options.

`status` reports MCP readiness, its endpoint and process, the app-data root, and the saved Avatar URL. Avatar connectivity is **unchecked**. MCP readiness does not mean the Engine is running or paired.

## Pair once

Try an ordinary action first. If no usable pairing exists:

1. Ask the assistant to call `avatar_pairing_start`.
2. Read the six-digit code in the Avatar Engine window.
3. Give the code to the assistant, which calls `avatar_pairing_complete` with the code and a client name.
4. Request your action again.

The pairing completion tool stores the credential privately. The assistant does not receive a bearer token or installation key. The six-digit code is temporary, not the saved credential.

Pairing through Avatar Settings or MCP works for both when they use the same app-data root. Subsequent actions restore pairing and activation internally, including after MCP restarts. There is no separate connect tool to call before each action. An Engine restart also works if its pairing registry survives.

Every assistant connected to this MCP server acts through one installation identity. **Shared** and **Exclusive** retain their Engine meanings. Explicit `avatar_activate` saves its chosen access mode only after successful activation and only if the saved target has not changed.

## Tools and effects

The catalog contains 25 direct Engine controls plus `avatar_speak` for text synthesis through Assistant. MCP arguments use the names advertised in each tool's schema. The server translates them into the existing HTTP contracts.

| Tool | Engine operation after `/avatarbridge` |
| --- | --- |
| `avatar_identity` | `GET /identity` |
| `avatar_pairing_start` | `POST /pairing/start` |
| `avatar_pairing_complete` | `POST /pairing/complete` |
| `avatar_activate` | `POST /activate` |
| `avatar_speech_queue_file` | `POST /speech/queue-file` |
| `avatar_speech_queue` | `POST /speech/queue` |
| `avatar_speech_stop` | `POST /speech/stop` |
| `avatar_speech_volume` | `POST /speech/volume` |
| `avatar_presentation_settings` | `POST /presentation/settings` |
| `avatar_message_text` | `POST /message/text` |
| `avatar_message_display` | `POST /message/display` |
| `avatar_message_clear` | `POST /message/clear` |
| `avatar_list` | `GET /avatar/list` |
| `avatar_select` | `POST /avatar/select` |
| `avatar_hair_list` | `GET /hair/list` |
| `avatar_hair_select` | `POST /hair/select` |
| `avatar_clothing_list` | `GET /clothing/list` |
| `avatar_clothing_select` | `POST /clothing/select` |
| `avatar_list_emotions` | `GET /emotion/list` |
| `avatar_set_emotion` | `POST /emotion/select` |
| `avatar_set_background_color` | `POST /background/color` |
| `avatar_set_resolution` | `POST /resolution` |
| `avatar_set_graphics_preset` | `POST /graphics/preset` |
| `avatar_health_check` | `GET /health` |
| `avatar_shutdown` | `POST /shutdown` |

Direct Engine controls affect the live Engine without saving appearance preferences or applying the web host's entire saved appearance before each action. Save durable preferences through [Avatar Settings](references/avatar-engine-settings.md). A later web settings synchronization can replace MCP's live changes.

The `avatar_speech_queue` and `avatar_speech_queue_file` tools queue prepared audio. File names refer to files on the **Engine machine**, not the assistant's machine. Audio URLs must be reachable from the Engine. See the [Engine HTTP API](references/avatar-engine-http-api.md) for field meanings and limits.

Shutdown requires an explicit accepted Boolean confirmation through the calling client's MCP elicitation support. Declining, cancelling, missing support, or changing the target while confirmation is open prevents shutdown. Stateless clients cannot use this confirmation flow in this release. Closing the Engine does not stop MCP.

## Speak text with the operator's provider

Call `avatar_speak` with one required `text` argument, for example `{"text":"Hello from Oscova."}`. It uses the single operator's selected speech provider, model, endpoint, credential, and voice from Speech settings. Browser speech output switches do not control this explicit request.

1. Start Oscova Assistant on the MCP machine with the same app-data directory.
2. Run `oscova login --host <host-url>` and sign in as the operator.
3. Select an available synthesis provider and save a compatible voice in Speech settings.
4. Call `avatar_speak`. Assistant applies saved Avatar settings, generates a complete clip, and sends its reachable URL to the Engine queue.

The tool remains listed when prerequisites are missing. Invocation reports how to start the host, repair login, align app-data roots, or correct provider and pairing settings. Provider selection respects the operator's local-only policy and does not fall back to another provider. A successful response means Engine acceptance, not completed playback. Check the Engine before retrying a cancelled or lost response.

The built-in Avatar tool shares the same operation. Its qualified name is `avatar__speak`. An MCP server named `Avatar MCP` imports the tool as `avatar_mcp__avatar_speak`.

## Shared files and development runs

The CLI Host and CLI Avatar MCP automatically use `.oscova/app_data` under the same user profile. `OSCOVA_APP_DATA_DIR` selects a different shared directory when set. Both modes share:

- `settings/avatar.yaml` for connection preferences.
- `settings/avatar-bridge-credentials.yaml` for the encrypted credential.
- `avatar-bridge.key` for the installation key.

A Host started directly from a development or published application still defaults to its package-relative `app_data`. Set `OSCOVA_APP_DATA_DIR` to the same absolute directory before launching a direct Host if it should share pairing with CLI Avatar MCP. Do not copy only the credential file without its matching key. Runtime discovery under `~/.oscova/avatar` does not replace these pairing files.

Stopping MCP preserves all three files. A stopped MCP server must be started explicitly again after a reboot. There is one managed MCP process and one saved Avatar target per CLI installation.

## Trust and troubleshooting

> **Warning:**
> The listener trusts local processes and has no separate inbound login. Do not expose it through a reverse proxy, tunnel, or port forward. The first release binds only to IPv4 loopback and rejects unexpected Host and Origin values.

The Avatar Engine may be on another trusted LAN machine, but an assistant on another machine cannot directly reach this MCP listener. Run MCP beside the assistant and save the LAN Engine URL instead. Requests are limited to 64 KiB.

- **MCP starts but actions fail:** start the Engine and check the saved URL. Then follow pairing guidance if needed.
- **Pairing appears lost:** compare app-data roots and check the matching key and encrypted credential. A reset Engine registry or changed Engine identity requires a fresh code.
- **Access conflict:** resolve Shared/Exclusive ownership at the Engine. MCP does not silently change modes or restart it.
- **Start fails:** run `status`, choose another port if needed, and inspect the system log directory reported by the CLI. Another listener is never stopped to free the port.
- **Action times out:** its effect may be unknown. MCP does not automatically resend ambiguous actions such as queued speech.
- **Storage fails:** fix read/write permissions on the shared directory. A failed credential save requires pairing again with a new code.
- **Shutdown is refused:** use a client with supported session-based Boolean elicitation, or close the Engine yourself.

## Related articles

- [Connect the Engine](references/avatar-engine-connecting.md)
- [Troubleshooting](references/avatar-engine-troubleshooting.md)
- [MCP Servers](references/user-guide-connections-mcp-servers.md)
