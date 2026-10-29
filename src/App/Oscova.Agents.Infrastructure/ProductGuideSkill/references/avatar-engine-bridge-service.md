# Bridge Service

The bridge service is the host-side client that talks to the separate Oscova Avatar Engine process. It keeps the Blazor UI and chat runtime from calling the engine directly.

> **Note:**
> The host proxy is Oscova's internal `api/avatarbridge` surface. The engine API is the external `/avatarbridge` surface documented in [Engine HTTP API](references/avatar-engine-http-api.md).

The bridge service owns Oscova's connection to one Avatar Engine. It pairs once, protects the issued bearer token on disk, activates the requested access mode for each engine run, and forwards authenticated commands.

## Responsibilities

The bridge service can:

- Pair the Oscova installation and reconnect it after either application restarts.
- Activate Shared or Exclusive access and probe the current engine session.
- List avatars and emotions from the running engine.
- Select avatars and emotions.
- List and select the hair and clothing options configured for a specific avatar.
- Queue speech clips by URL or engine-local file path.
- Stop speech and clear the engine speech queue.
- Apply window, background, graphics, message, camera, and audio settings.
- Show or clear message-panel text without audio.
- Request engine shutdown.

The bridge client does not launch the Avatar Engine process. A separate Host-local action can launch the managed Windows executable before the bridge client connects.

## Host proxy routes

The host exposes routes under `api/avatarbridge`. Important proxy routes include:

| Host route | Purpose |
| --- | --- |
| `GET api/avatarbridge/settings` | Load saved avatar settings. |
| `PUT api/avatarbridge/settings` | Save avatar settings without changing the live engine. |
| `GET api/avatarbridge/local-engine/installed` | Report whether the exact managed Windows executable exists. |
| `POST api/avatarbridge/local-engine/start?bridgeUrl=...` | Start the managed executable on the port from a loopback HTTP URL. |
| `POST api/avatarbridge/sync` | Connect and apply saved settings to the live engine. |
| `PUT api/avatarbridge/settings/connection?baseUrl=...&accessMode=...` | Save only the connection URL and preferred access mode. |
| `POST api/avatarbridge/pairing/start?baseUrl=...` | Ask the engine to display a new pairing code. |
| `POST api/avatarbridge/pairing/complete` | Exchange the user-entered code and protect the issued credential. |
| `GET api/avatarbridge/health` | Check engine health through the current bridge client. |
| `GET api/avatarbridge/avatars` | List available avatars. |
| `POST api/avatarbridge/avatars/select` | Select an avatar. |
| `GET api/avatarbridge/hair?avatarName=...` | List hair options for an avatar. |
| `POST api/avatarbridge/hair/select` | Select hair for an avatar. |
| `GET api/avatarbridge/clothing?avatarName=...` | List clothing options for an avatar. |
| `POST api/avatarbridge/clothing/select` | Select clothing for one avatar slot. |
| `GET api/avatarbridge/emotions` | List available emotions. |
| `POST api/avatarbridge/emotions/set` | Select an emotion through the host proxy. |
| `POST api/avatarbridge/speech/speak` | Synthesize operator text and queue its audio URL. |
| `POST api/avatarbridge/speech/queue` | Queue a `/tts/` clip URL for avatar speech. Optional `baseUrl` must match the connected Engine. |
| `POST api/avatarbridge/speech/queue-file` | Queue an engine-local audio file. |
| `POST api/avatarbridge/speech/stop` | Stop playback and clear queued speech. Optional `baseUrl` targets an Engine previously queued through this Host, even after the current connection changes. |
| `POST api/avatarbridge/speech/volume` | Set avatar playback volume. |
| `POST api/avatarbridge/message/text` | Show message text without audio. |
| `POST api/avatarbridge/message/clear` | Clear the message panel. |
| `POST api/avatarbridge/presentation/settings` | Apply Auto, Subtitle, or Side mode with both complete visual layouts. |
| `POST api/avatarbridge/resolution` | Set width, height, and fullscreen mode. |
| `POST api/avatarbridge/background/color` | Set background RGB values. |
| `POST api/avatarbridge/graphics/preset` | Set quality level and runtime ray tracing. |
| `POST api/avatarbridge/shutdown?expectedBaseUrl={url}` | Shut down the confirmed Engine. The URL must match the current connection. A changed target is refused and requires a new confirmation. |

Some host proxy names differ from the direct engine API. For example, `api/avatarbridge/emotions/set` forwards to the engine's `/avatarbridge/emotion/select`.

Emotion requests accept `Intensity`, a finite strength from `0` to `1`, inclusive. The host proxy forwards it unchanged. Omitted values default to `1`, and explicit `0` removes emotion influence while speech and blinking continue. `BlendSpeed` remains the transition duration in seconds. The C# call is `SetEmotionAsync(emotionName, blendSpeed, intensity: 0.5f)` and defaults to full intensity when the third argument is omitted.

The bridge service rejects negative, above-one and non-finite intensity before serialization or HTTP transport, returning `Success = false` and `Description = "Intensity must be a finite number between 0 and 1."` The proxy preserves its normal response status convention and returns that result in an HTTP 200 response. Success echoes the accepted intensity. Failure uses `Intensity = 0` without reporting current scene state. Intensity is request-scoped, and settings synchronization continues to request full intensity.

`presentation/settings` accepts `Mode`, shared `DurationSeconds`, and both `Subtitle` and `Side` layouts. Each layout contains `Yaw`, normalized `PositionX` and `PositionY`, `Width`, `Height`, `FontSize`, and normalized RGBA values. The endpoint validates and applies the complete request together.

The two `local-engine` routes belong only to the Oscova Host. They are not direct Unreal `/avatarbridge/*` routes. Local start always passes `-BridgeAddress=127.0.0.1` and the effective port from `bridgeUrl`. It rejects unsupported operating systems and URLs that are not absolute `http` URLs for `localhost` or `127.0.0.1`.

The installed check returns `false` outside Windows or when the exact executable is absent. Start returns `204 No Content` after process creation and the immediate exit check, `400 Bad Request` for an unsupported platform or URL, `404 Not Found` when the executable is absent, and a problem response when launch fails.

## Text speech through Assistant

`POST /api/avatarbridge/speech/speak` is a Host-only operation. Send `{"text":"Hello"}` as an authenticated operator. The host uses that operator's provider, credentials, and voice selected in Speech settings, ensures Avatar readiness, and forwards the generated audio URL to the Engine's existing `POST /avatarbridge/speech/queue` route. An unchanged ready connection preserves live appearance and volume changes.

The optional `expectedAppDataPath` must be an absolute path matching the host's app-data directory. Avatar MCP always sends its own directory to prevent an installation mismatch. The body cannot select another user, provider, voice, or Engine.

Empty or emoji-only text and invalid paths return `400`. A different app-data directory returns `409` with `host_mismatch`. Authentication requires the operator's `Admin` role. Other operation outcomes return `200` with `success`, `description`, and an optional `errorCode`, preserving Engine result fields. Always inspect `success`. A provider failure does not trigger a fallback, and local-only policy still applies.

Success establishes queue acceptance, not playback completion. A cancelled or lost response is not automatically retried because audio may already have been queued.

## Authorization

The host proxy controller requires the `Admin` role. Unauthenticated requests are rejected, and authenticated non-admin users are forbidden. This applies to settings and runtime proxy calls because they control the shared avatar runtime.

The engine's own `/avatarbridge` API is a separate process. Its public identity and pairing routes do not require a credential. All command routes require a valid paired bearer token and an activation for the current engine run.

Shared mode allows multiple paired clients. Exclusive mode allows one client and rejects additional activation until the engine restarts. An Exclusive client cannot displace Shared clients that already activated.

The Host pairing responses never expose the bridge token. Only Infrastructure receives it from the engine.

The host pairing-completion body contains `baseUrl` and `pairingCode`. It does not use the direct engine's `clientName` field. Oscova supplies its own client name, protects the issued credential, then activates through saved-settings synchronization. Pairing routes return an operation result with `success`, `description`, `errorCode`, and an optional `retryAfterSeconds`. Starting pairing also returns `expiresInSeconds` on success. A valid HTTP response can still contain `success: false`.

## Credential storage

Oscova stores one paired Engine credential:

- `app_data/settings/avatar-bridge-credentials.yaml` contains the bridge URL, Engine ID, client ID, and protected token envelope.
- `app_data/avatar-bridge.key` contains the random installation key used to protect that token.
- `avatar.yaml` contains the nonsecret preferred access mode with the rest of the Avatar settings.

The token uses AES-GCM protection tied to the normalized URL and both IDs. This protects the settings file from casual disclosure, but anyone who can read both local files can recover the token. It does not use or wait for the user's Vault Password.

Pairing a different Engine replaces the one saved Oscova credential. The Engine stores its own client records beside its runtime configuration under `Saved/AvatarBridge/pairings.json`.

A missing, unreadable, or invalid saved credential is treated as requiring pairing. A different normalized URL or Engine ID also requires pairing. If saving the newly issued token fails, Oscova reports `credential_persistence_failed`. Correct the local storage permissions and pair again with a new code.

## Readiness flow

Saved settings are not the same as a ready engine. `ApplySavedSettingsAsync` probes the saved authenticated connection before trusting its ready cache. A healthy retained session reuses the same client. An engine restart returns `activation_required`, so Oscova activates in place, changes the local session version, and reapplies the full saved snapshot.

The ready flag is cleared when avatar settings are saved. If speech queueing fails during a reply, that reply stops sending more avatar clips and the next reply gets a fresh readiness attempt.

Readiness is tied to the normalized bridge URL, the active engine session, and a fingerprint of the saved settings snapshot. If any changes, Oscova reconnects or reapplies as needed. Concurrent readiness attempts are serialized so multiple speech paths do not stampede the engine.

> **Important:**
> Saving can succeed while the engine is offline. Applying, previewing, speech queueing, and shutdown require a reachable engine.

## Saved settings sync

The saved-settings sync marks the runtime ready only after every command succeeds:

- If the bridge URL is blank after normalization, sync fails.
- If pairing, activation, or the session probe fails, sync preserves the engine error code and retry delay.
- If a runtime command is rejected, sync fails with `Connected = true` and `SettingsApplied = false`.
- If every command succeeds, sync returns `Success = true`, `Connected = true`, and `SettingsApplied = true`.

Avatar and emotion selection are skipped during sync when the saved value is blank. All other durable visual/audio settings are always sent.

Commands are applied sequentially. A failure does not roll back settings already sent to the engine. Correct the failed value or connection problem and retry the saved-settings apply. The host returns `400 Bad Request` with the sync result when synchronization fails.

## Error handling

Most bridge calls return an avatar response with `Success` and `Description`. Controller failures that happen before the engine response become HTTP problem responses and are logged by the host. The speech queue proxy also rejects URLs that are not absolute `http` or `https` URLs pointing at `/tts/` clips.

Hair and clothing operations require an explicit avatar name. The host does not infer or store a current avatar for these APIs. Clothing selection also requires the `top` or `bottom` slot. Hair and clothing selections are session-scoped and are not included in saved-settings sync.

The bridge service converts HTTP, JSON, or disconnected-client failures into non-success avatar responses where possible. For callers, a `200 OK` with `Success = false` means the proxy ran but the bridge/engine rejected the operation. A `400 Bad Request` usually means Oscova rejected the request before forwarding it.

## Related articles

- [Avatar Settings](references/avatar-engine-settings.md)
- [Engine HTTP API](references/avatar-engine-http-api.md)
- [Speech and Avatar Flow](references/avatar-engine-speech-flow.md)
- [Troubleshooting](references/avatar-engine-troubleshooting.md)
