# Give OpenClaw, Hermes Agent or any MCP agent a 3D avatar

Give your OpenClaw, Hermes Agent or other MCP agent a realistic 3D face that speaks, with the free Oscova Avatar Engine. You set it up once. After that, ask your agent in plain words, and it picks an emotion and speaks through the avatar on your screen.

Your agent writes the words. Oscova Agents turns them into speech with your chosen voice. The Avatar Engine shows the avatar and moves its lips to the speech.

The agent connects through the Avatar MCP server. MCP (Model Context Protocol) is the standard way agents discover and call tools.

## Watch OpenClaw and Hermes Agent speak through the avatar

OpenClaw avatar demo:

> [!Video https://www.youtube.com/embed/WI-iwNQMZyk]

Hermes Agent avatar demo:

> [!Video https://www.youtube.com/embed/xQtLaYxmR8k]

## What you need

- A Windows PC to run the Avatar Engine.
- OpenClaw, Hermes Agent (by Nous Research) or another MCP agent on the same PC.
- Oscova Agents on the same PC. Step 1 installs it.

## 1. Install Oscova Agents

1. Run the install command in PowerShell:
   
   ```powershell
   irm https://oscova.ai/install.ps1 | iex
   ```

2. When Oscova lists optional plugins, select **Local Speech Synthesis** for a voice that runs on your PC.

3. Accept the Avatar Engine installation when Oscova offers it. To install it later, run `oscova avatar install`.

4. Finish [first-time setup](references/user-guide-getting-started-first-time-setup.md) in your browser. Setup downloads the local speech models.

## 2. Choose a voice

Pick and save a voice as described in [Choose and test a voice](references/user-guide-getting-started-voice.md). The avatar always speaks with your saved voice.

## 3. Start and pair the Avatar Engine

1. Start the Engine:
   
   ```text
   oscova avatar start
   ```

2. In Oscova, pair it once as described in [Connect the Engine](references/avatar-engine-connecting.md). You enter the six-digit code that the Engine window shows.

Oscova saves the pairing. Your agent reuses it, so it never needs the code.

## 4. Start the Avatar MCP server

```text
oscova login
oscova avatar mcp start
```

`oscova login` lets the MCP server use your Oscova voice. The server listens on `http://127.0.0.1:11319/mcp` and keeps running when you close the terminal.

## 5. Connect your agent

### OpenClaw avatar setup

Add the `mcp` section to `~/.openclaw/openclaw.json`, next to your existing settings:

```json
{
  "mcp": {
    "servers": {
      "avatar": {
        "url": "http://127.0.0.1:11319/mcp",
        "transport": "streamable-http"
      }
    }
  }
}
```

Then start a new OpenClaw session, for example with `openclaw chat`.

### Hermes Agent avatar setup

Add the `mcp_servers` section to `~/.hermes/config.yaml`:

```yaml
mcp_servers:
  avatar:
    url: http://127.0.0.1:11319/mcp
```

Then start Hermes. To try it with only the avatar tools, run `hermes -t mcp-avatar`.

### Other MCP agents

Add an HTTP (Streamable HTTP) MCP server with the endpoint `http://127.0.0.1:11319/mcp` in your agent's MCP settings. The agent must run on the same PC as the Avatar MCP server.

## 6. Try it

Ask your agent, for example:

```text
Turn these notes into a short spoken update and say it through my avatar with one fitting emotion: the payments fix is on staging, and QA signs off today.
```

The agent sets an emotion and calls `avatar_speak`. The avatar speaks the update with your voice.

## What the avatar can do

Your agent can make the avatar:

- Speak text with your saved voice.
- Show an emotion, such as happy or focused, at a chosen strength.
- Play a gesture.
- Switch to another avatar, hair style or outfit.
- Change the background color or show a text message.

The [Avatar MCP server](references/avatar-engine-mcp-server.md) article lists all 28 tools.

## Give the agent only the tools it needs

To speak with emotions and gestures, an agent needs five tools. Add a filter to the `avatar` entry to offer only these.

OpenClaw:

```json
"avatar": {
  "url": "http://127.0.0.1:11319/mcp",
  "transport": "streamable-http",
  "toolFilter": {
    "include": [
      "avatar_speak",
      "avatar_list_emotions",
      "avatar_set_emotion",
      "avatar_list_gestures",
      "avatar_select_gesture"
    ]
  }
}
```

Hermes Agent:

```yaml
mcp_servers:
  avatar:
    url: http://127.0.0.1:11319/mcp
    tools:
      include:
        - avatar_speak
        - avatar_list_emotions
        - avatar_set_emotion
        - avatar_list_gestures
        - avatar_select_gesture
```

## Let your agent speak on its own

Your agent uses the avatar when you ask. To make it speak without being asked, add a standing instruction:

- OpenClaw: `~/.openclaw/workspace/AGENTS.md`
- Hermes Agent: `~/.hermes/SOUL.md`

For example:

```text
When you finish a task that took more than a few minutes, announce the result in one or two sentences with avatar_speak.
```

Save the file, then start a new agent session.

## Frequently asked questions

### Is the avatar free?

Yes. The Oscova Avatar Engine is a free Windows app. A cloud speech provider or your agent's own model may charge for use.

### Does my text leave my PC?

Not for the avatar's speech when you use **Local Speech Synthesis**. Oscova makes the speech on your PC, and the Engine renders the avatar there. Your agent's own model follows that agent's settings.

### Which voice does the avatar use?

The voice saved in your Oscova speech settings. Change it there, and the agent's next sentence uses the new voice.

### Can I use another MCP agent?

Yes. Any MCP client that supports HTTP servers can connect at `http://127.0.0.1:11319/mcp` from the same PC.

## More from Oscova Agents

The avatar is part of Oscova Agents, a private AI agent OS. Its own agents keep chats, memory, instructions and settings encrypted under a Vault Password only you hold. The [Egress Audit](references/user-guide-monitoring-egress-audit.md) shows what Oscova's agents sent off your machine.

See [Get to know Oscova](references/user-guide-getting-started-get-to-know-oscova.md).

## Troubleshooting

- **The agent shows no avatar tools:** run `oscova avatar mcp status` and check that the URL in your agent's settings matches its endpoint.
- **Avatar actions fail:** start the Engine with `oscova avatar start`.
- **Actions report that pairing is required:** pair again in Oscova as in step 3. A reset Engine needs a fresh code.
- **`avatar_speak` fails:** check that Oscova is running, save a voice in speech settings, then run `oscova login` again. The error message names the missing step.

For more detail, see [Avatar MCP server](references/avatar-engine-mcp-server.md).

## Related articles

- [Avatar MCP server](references/avatar-engine-mcp-server.md)
- [Connect the Engine](references/avatar-engine-connecting.md)
- [Voice](references/user-guide-getting-started-voice.md)
