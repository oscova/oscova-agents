# Speech and Avatar Flow

Avatar speech is the path from generated text to speech synthesis, queued audio playback, message-panel text, and lip sync in the Avatar Engine.

> **Note:**
> Text-to-speech, often shortened to TTS, means converting written text into spoken audio.

## Output modes

Admins can choose where assistant speech plays:

- **Browser only** plays speech in the web browser.
- **Browser and avatar** plays speech in the browser and queues the same clips to the avatar.
- **Avatar only** queues speech to the avatar without browser audio.

Non-admin users resolve to browser-only output even when the saved admin setting includes the avatar.

## Queue model

The current engine speech path is a clip queue:

- `speech/queue` accepts an absolute audio URL plus the matching message text.
- `speech/queue-file` accepts an audio file already available on the engine machine.
- `speech/stop` stops current playback and clears queued speech.
- `speech/volume` changes avatar playback volume.

Queued clips play in order. The engine shows each clip's `Message` text while that clip plays, which keeps subtitles and lip sync aligned with audio.

Only the selected avatar processes speech. Switching avatars stops current playback and clears the outgoing avatar's queue. New clips play on the newly selected avatar. Reselecting the same avatar does not interrupt speech.

With the two presentation layouts configured and Auto selected, the first speech sentence that exceeds the Subtitle box's available height moves the presentation to Side. Following sentences keep Side even when their text is short. The presentation returns to Subtitle when the message panel clears explicitly or its display timer expires. Each displayed sentence restarts that timer. This retains the existing timeout behavior between clips.

Oscova no longer uses a byte-stream speech route for the avatar path. It synthesizes a complete WAV clip, exposes it as a `/tts/` URL, and queues that URL.

## Chat read-aloud and voice mode

For chat read-aloud and voice mode, Oscova breaks the assistant reply into sentence-sized text chunks. Each chunk is synthesized into a WAV clip and exposed as a `/tts/` URL. Oscova then posts that absolute URL to `api/avatarbridge/speech/queue`, and the host bridge forwards it to `/avatarbridge/speech/queue` on the engine.

When output is **Browser and avatar**, browser playback is the timing source. Oscova waits until the browser starts a clip, then queues the same clip to the avatar.

When output is **Avatar only**, Oscova queues clips directly and uses synthesized clip durations to estimate when voice mode can resume listening.

The first avatar-routed clip in an assistant reply triggers one readiness attempt for that reply. If readiness fails, Oscova keeps browser speech working when browser output is enabled and skips avatar queueing for that reply. If a later avatar queue post fails, Oscova marks the runtime unready and stops posting more avatar clips until the next reply.

## Emotion cues in spoken replies

When the operator's speech output mode includes the avatar and the engine has a known emotion list, the agent may start a feeling sentence with `[emotion:NAME]` or use an emoji. Oscova matches tag names without regard to case and uses the engine's name for the emotion. An explicit tag takes priority over emoji in the same speech clip.

Known tags are removed from speech and chat text. Tags inside code formatting or written as `\[emotion:NAME]` stay literal. Unknown tags also remain visible. Emoji stay visible in chat.

The avatar changes expression as the tagged words play, holds it, then returns to the currently selected emotion. A repeated emotion restarts the hold time. Stopping speech or ending voice mode restores the selected emotion immediately. The timing controls are in [Avatar Settings](references/avatar-engine-settings.md).

## Voice mode timing

Voice mode has to know when it can listen again:

1. The user speaks.
2. Oscova transcribes the audio.
3. The agent replies.
4. Oscova queues synthesized speech.
5. Voice mode waits until the reply is complete, playback is complete, and no speech requests are pending.
6. Voice mode resumes listening.

In browser playback modes, the browser reports when playback finishes. In avatar-only mode, Oscova estimates completion from the clip's sample count, sample rate, channel count, or duration metadata.

## Agent tool speech

The built-in `avatar__speak(text)` and MCP `avatar_speak(text)` tools use one operator-only operation. It selects the operator's configured synthesis provider, model, endpoint, credential, and voice from Speech settings. Saving a new Speech voice affects the next request without saving Avatar settings.

The operation checks provider availability, applies saved Avatar settings, synthesizes one complete WAV clip in its own session, and queues a reachable audio URL on the captured Engine target. Its session cleanup leaves browser speech sessions alone. An unavailable provider, blocked local-only policy, or failed readiness check returns an actionable failure without switching providers.

MCP calls `POST /api/avatarbridge/speech/speak` on the matching running Assistant host using the saved operator CLI login. It remains discoverable while the host is stopped. See [Avatar MCP server](references/avatar-engine-mcp-server.md) for setup.

A successful result means the Engine accepted the request. It does not prove completed playback or lip sync. Cancellation or a lost response can leave the outcome uncertain, so inspect the Engine before retrying.

## Message text without audio

Message text is also available without speech:

- `message/text` shows text in the engine message panel.
- The direct engine `message/display` route shows text for a one-shot positive `durationSeconds`, without changing the saved global duration. It has no matching Oscova host proxy route.
- `message/clear` hides the message panel.
- The Avatar settings **Send Preview** and **Clear** buttons use these message endpoints.

This is useful when tuning the two saved presentation layouts and their shared display duration.

## Preconditions

Avatar speech requires:

- Speech synthesis to produce a clip with a reachable `/tts/` URL.
- A bridge URL that points at a running engine.
- Saved avatar settings that can be applied, or an already-ready runtime.
- A paired Oscova installation that can activate in the engine's current Shared or Exclusive mode.
- For automatic chat read-aloud, an operator output mode that includes the avatar. Explicit `avatar__speak` does not require browser speech to be enabled.

Non-admin users resolve to browser-only speech output, even if avatar output modes exist in settings.

## Stop behavior

When speech synthesis is turned off or voice mode stops after avatar speech has started, Oscova sends `speech/stop` through the host bridge. The engine is expected to stop current audio and lip sync and clear queued speech.

Keep `ForcedStop` set to `false` for the recommended graceful stop. Set it to `true` to interrupt immediately.

## Related articles

- [Bridge Service](references/avatar-engine-bridge-service.md)
- [Engine HTTP API](references/avatar-engine-http-api.md)
- [Avatar Settings](references/avatar-engine-settings.md)
- [Troubleshooting](references/avatar-engine-troubleshooting.md)
