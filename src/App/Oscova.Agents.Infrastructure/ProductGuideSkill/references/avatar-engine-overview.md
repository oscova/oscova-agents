# Avatar Engine

The Oscova Avatar Engine gives Oscova Agents a visible, speaking avatar. It runs as a separate program and exposes an HTTP API that the Oscova host calls when it needs to select a character, change expressions, queue speech, show message text, or adjust the rendered window.



> **Note:**
> An HTTP API is a set of web addresses that another program can call. The Avatar Engine owns the `/avatarbridge` API. Oscova Agents calls it through a host-side bridge service.

## Mental model

Think of the Avatar Engine as a second runtime beside Oscova:

- Oscova Agents stores settings, synthesizes speech, decides when an assistant reply should be spoken, and exposes the Blazor settings screen.
- The Avatar Engine renders the avatar, plays queued audio clips, drives lip sync, shows message-panel text, and applies live visual settings.
- The host bridge service is the client between them. It keeps one protected pairing credential and reconnects automatically after either program restarts.

The default local bridge URL is `http://127.0.0.1:11318`. Use that when both programs run on the same machine. Use a reachable LAN URL only when the engine is intentionally running on another computer.

> **Important:**
> Saving avatar settings does not start the engine or change the live engine. The running engine changes only when Oscova applies settings or sends a live command.

## What the engine controls

The engine is responsible for the visual runtime:

- The selected avatar model.
- The hair and clothing options available through the engine API for each avatar.
- The current emotion and transition speed.
- The rendered window, background, graphics preset, and selected presentation layout.
- The message panel used for subtitles, captions, previews, or announcements.
- Speech playback, clip queueing, volume, and lip sync.

Oscova Agents is responsible for configuration, speech synthesis, chat timing, and access control. The host stores avatar settings, applies them to the live engine on demand, and exposes internal `api/avatarbridge` routes for the Blazor settings screen.

## Setup path

For a first setup:

1. Start the Avatar Engine binary and leave it running.
2. Open **Settings** > **Avatar** in Oscova.
3. Confirm **Bridge URL** is `http://127.0.0.1:11318`, or enter the reachable engine address.
4. Choose **Shared** or **Exclusive**, then click **Connect**.
5. Enter the six-digit code shown by the Avatar Engine when prompted.
6. Choose an avatar, expression, window, subtitle and side layouts, audio, and voice configuration.
7. Click **Save Settings**.
8. Click **Apply to Live Avatar** to push the saved snapshot to the running engine.
9. Use **Send Preview** to confirm the message panel works without audio.

The apply step pushes durable runtime state in one pass: resolution, graphics preset, background color, both presentation layouts and the selected mode, speech volume, selected avatar, and selected emotion.

Pairing survives ordinary restarts. After an Avatar Engine restart, Oscova activates the saved client again and reapplies the saved runtime settings before reporting that it is ready.

## How speech reaches the avatar

Oscova uses a clip queue. A clip is a complete synthesized audio file plus the matching text. The engine receives each clip through `POST /avatarbridge/speech/queue`, downloads the URL, and plays queued clips in order.

Chat read-aloud and voice mode synthesize sentence-sized clips so the avatar can begin speaking before the full answer is complete. When both browser and avatar playback are enabled, the browser starts each clip first and Oscova then queues the same clip to the avatar for timing alignment. In avatar-only mode, Oscova queues clips directly and estimates completion from the synthesized audio duration.

The `avatar__speak` tool also uses the queue path: it synthesizes the requested text, builds an absolute `/tts/` URL, and sends that URL to the engine.

> **Note:**
> A `/tts/` URL is a temporary audio URL served by the Oscova host. If the Avatar Engine is on another machine, that machine must be able to reach the Oscova host URL it receives.

## Message panel

The message panel is not only a subtitle. It can show:

- Speech text while the avatar talks.
- A caption or accessibility note without audio.
- A persistent announcement when display duration is set to `0`.
- Preview text from Avatar settings.
- A temporary message with its own duration through the direct `message/display` API.

Use `message/text` to show text without audio and `message/clear` to hide it. A queued speech clip with non-empty `Message` text replaces the panel text while that clip plays.

Pairing uses the same message panel to show its six-digit code and a brief confirmation. It keeps the panel's configured appearance and position.

## Security and ownership

The Avatar settings and host proxy routes are admin-only in Oscova. Regular users can still chat, but the app resolves their assistant speech output to browser playback only. This keeps the shared avatar runtime from being controlled independently by every account.

Avatar settings are shared system settings. Changing the bridge URL, selected avatar, message box, camera, or volume changes the shared avatar runtime configuration for the application. Speech uses the operator's selected provider and voice from Speech settings.

## Where to go next

- [Connect the Engine](references/avatar-engine-connecting.md) explains the first setup path.
- [Avatar Settings](references/avatar-engine-settings.md) documents every visible setting.
- [Bridge Service](references/avatar-engine-bridge-service.md) explains the host-side proxy and readiness behavior.
- [Engine HTTP API](references/avatar-engine-http-api.md) lists the engine endpoints and JSON shapes.
