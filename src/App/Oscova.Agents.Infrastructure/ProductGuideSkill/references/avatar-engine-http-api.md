# Engine HTTP API

The Oscova Avatar Engine exposes an HTTP server, usually at `http://127.0.0.1:11318`. Oscova Agents calls this server to select avatars, queue speech, show message-panel text, and apply visual settings.

> **Note:**
> An HTTP API is a set of callable web addresses. The engine API is owned by the Avatar Engine binary. Oscova's internal host proxy is documented in [Bridge Service](references/avatar-engine-bridge-service.md).

All engine routes use the `/avatarbridge` prefix. JSON responses use the engine's standard lower-camel field names. The complete presentation request requires the PascalCase field names shown in its example.

## Base URL

For a local engine, the base URL is usually:

```text
http://127.0.0.1:11318
```

An engine endpoint is the base URL plus the route. For example:

```text
http://127.0.0.1:11318/avatarbridge/health
```

Oscova's host proxy routes start with `api/avatarbridge`. Those are not the engine's public route names.

## Pairing and authorization

Pair once to receive a durable client credential:

1. Call `GET /avatarbridge/identity` and retain the nonsecret `engineId` with your saved credential.
2. Call `POST /avatarbridge/pairing/start`. The engine shows a six-digit code through its configured message panel for the code's remaining lifetime and never returns the code over HTTP.
3. Ask the user for that code, then call `POST /avatarbridge/pairing/complete` with a client name.
4. Save the returned `clientId` and `bridgeToken`. The token is returned only once. After the credential is saved, the engine replaces the code with `Paired` for five seconds.

```json
{ "pairingCode": "123456", "clientName": "Oscova Assistant - Office PC" }
```

```json
{
  "success": true,
  "description": "Avatar Engine paired.",
  "engineId": "0f3fae72-5253-4db4-b607-bbc966f07229",
  "clientId": "1592be03-d11f-4012-a73d-9a92c033c7b2",
  "bridgeToken": "v1.<credential>"
}
```

Send the token on authenticated requests:

```http
Authorization: Bearer <BridgeToken>
```

An authenticated client can call `health` before activation. Before sending any command, call `POST /avatarbridge/activate` with either `Shared` or `Exclusive` access:

```json
{ "accessMode": "Shared" }
```

Shared mode lets multiple paired clients activate during the same engine run. Exclusive mode admits one client. The selected mode cannot change until the engine restarts. Activation is memory-only and resets on restart, while paired credentials remain valid.

There is one pending pairing code for the engine. Calling `pairing/start` again while it is valid redisplays that code and returns its remaining `expiresInSeconds`, without extending its lifetime. A successful completion consumes it. Another client must then start pairing again.

The public `identity`, `pairing/start`, and `pairing/complete` routes are independently rate limited by client IP address to 60, 3, and 10 requests per minute. A rejected request returns `429 Too Many Requests` with `Retry-After`.

Missing, malformed, or unknown credentials return the same `401 Unauthorized` response with `errorCode` set to `pairing_required` and `WWW-Authenticate: Bearer realm="AvatarBridge"`. A valid client that has not activated receives `409 Conflict` with `activation_required` when it calls a command route, unless another client owns Exclusive access, which returns `403 Forbidden` with `exclusive_access_denied`.

Pairing and activation failures use these status codes:

| Status | `errorCode` | Meaning |
| ---: | --- | --- |
| `400` | `invalid_json` | The body is not valid JSON. |
| `400` | `invalid_request` | A required field has the wrong JSON type. |
| `400` | `pairing_code_missing` | `pairingCode` is absent. |
| `400` | `pairing_code_malformed` | The code is not exactly six digits. |
| `400` | `client_name_invalid` | The client name is absent or violates its length or control-character rules. |
| `400` | `pairing_code_invalid` | The well-formed code is incorrect and attempts remain. |
| `400` | `access_mode_invalid` | The access mode is not exactly `Shared` or `Exclusive`. |
| `401` | `pairing_required` | The bearer credential is missing or invalid. |
| `403` | `exclusive_access_denied` | Another client owns Exclusive access. |
| `409` | `pairing_not_started` | No pending code exists or the code was already consumed. |
| `409` | `activation_required` | The paired client has not activated for this engine run. |
| `409` | `exclusive_unavailable` | Shared mode is already active. |
| `409` | `access_mode_conflict` | The Exclusive owner requested Shared mode. |
| `410` | `pairing_code_expired` | The pending code expired. |
| `413` | `request_too_large` | The pairing or activation body exceeds its route limit. |
| `429` | `pairing_attempts_exhausted` | Five wrong codes cancelled the pending session. |
| `429` | `rate_limited` | The client IP exceeded this public route's limit. |
| `500` | `pairing_persistence_failed` | A new credential could not be saved. |
| `503` | `pairing_registry_unavailable` | The durable registry is unavailable or invalid. |
| `503` | `peer_address_unavailable` | The public route could not determine a client IP. |
| `503` | `crypto_unavailable` | Secure random generation or hashing failed. |

The engine stores only token verifiers in `Saved/AvatarBridge/pairings.json`. There is no reset endpoint or UI. To invalidate every credential, stop the engine, delete that file, and restart. The engine creates a new identity and all clients must pair again.

> **Warning:**
> The engine uses ordinary HTTP. Treat non-loopback access as trusted-network only because bearer tokens are not protected from network observers.

## Naming rules

Engine paths follow these rules:

- Enumerable named state uses `{domain}/list` to read and `{domain}/select` to choose.
- Scalar or structured properties use `{domain}/{property}` with `POST`.
- Lifecycle actions use imperative names, such as `activate`, `speech/stop`, `message/clear`, or `shutdown`.
- The complete visual presentation is configured atomically through `presentation/settings`.
- Status reads use `health`.

Speech uses a queue model. `speech/queue` enqueues an audio URL, and `speech/queue-file` enqueues a file already available to the engine.

Only the selected avatar processes speech. Selecting a different avatar stops the outgoing avatar's playback and clears its queued clips. Selecting the same avatar leaves its speech running.

## Response shape

Most command responses include:

```json
{ "success": true, "description": "ok" }
```

Some responses add the applied value, such as `selectedAvatar`, `selectedEmotion`, `volume`, `width`, `height`, or `savedFilePath`. Protocol failures use one stable shape:

```json
{ "success": false, "errorCode": "pairing_required", "description": "A valid Avatar Bridge credential is required." }
```

`health`, `avatar/list`, `hair/list`, `clothing/list`, and `emotion/list` return status or catalog data instead of a single applied setting.

Check both the HTTP status and `success`. Runtime command handlers can report validation, download, or asset-loading failure in their JSON response without a protocol-level HTTP error. Their failures generally contain `description`, rather than the pairing protocol's `errorCode`.

A successful `hair/select` response means the avatar and hair IDs were validated, the configured runtime assets loaded, and the runtime event was dispatched. A successful `clothing/select` response similarly means the avatar, slot, and clothing ID were validated and dispatched. These responses do not provide a visual completion acknowledgement. Hair and clothing selections last only for the current engine session and are not saved as Oscova settings.

## Message panel behavior

The message panel is a general text surface. It can be used for subtitles, captions, persistent announcements, or settings previews.

- `message/text` replaces the current panel text without audio.
- `message/display` replaces the current panel text for the request's one-shot `durationSeconds` without changing the global display duration.
- `message/clear` hides the panel.
- A queued speech clip with a non-empty `message` replaces the panel text while that clip plays.
- A queued speech clip with an empty `message` leaves the current panel text alone.
- `presentation/settings` sets Auto, Subtitle, or Side mode, both layouts, and the shared display duration used by ordinary message text and speech messages.
- `pairing/start` displays the engine-generated code through this same configured panel for its remaining lifetime. Successful pairing replaces it with `Paired` for five seconds. Neither action changes the panel position, style, opacity, or global duration.

## Validation

The engine returns a non-success response for invalid input instead of silently fixing the value.

| Setting | Accepted values |
| --- | --- |
| `pairing/complete` | Body up to 1 KiB. `pairingCode` is six ASCII digits after trimming. `clientName` is 1 through 100 trimmed characters with no control characters. The code lasts two minutes, is one-use, and is cancelled after five well-formed wrong attempts. |
| `activate` | Body up to 256 bytes. `accessMode` is exactly `Shared` or `Exclusive`. |
| `speech/volume` | `0.0` through `1.0` |
| `message/text` | Non-empty `message` after trimming |
| `message/display` | Non-empty `message` after trimming and finite `durationSeconds > 0` |
| `presentation/settings` | `mode` is `Auto`, `Subtitle`, or `Side`; duration is finite and `>= 0`; each layout yaw is `-5` through `5`; font size is `8` through `120`; position and RGBA values are finite `0` through `1`; width and height are finite `(0, 1]`; each rectangle must fit within the screen |
| `background/color` | RGB components `0` through `255` |
| `resolution` | width `1` through `7680`, height `1` through `4320` |
| `emotion/select` | case-sensitive emotion name, `blendSpeed > 0`, and finite `intensity` from `0` to `1` inclusive (default `1`) |
| `avatar/select` | case-sensitive avatar name |
| `hair/list` | required case-sensitive `AvatarName` query value |
| `hair/select` | case-sensitive `avatarName` and a `hairId` returned for that avatar |
| `clothing/list` | required case-sensitive `AvatarName` query value |
| `clothing/select` | case-sensitive `avatarName`, `slot` set to `top` or `bottom`, and a `clothingId` returned for that avatar and slot |
| `graphics/preset` | `level` from `0` through `4`, mapping to Low, Medium, High, Epic, and Cinematic. Missing or other levels are rejected |

`speech/queue` requires a non-empty URL beginning with `http://` or `https://`. The download must succeed with a 2xx response and non-empty content. The engine recognizes MP3, WAV, FLAC, Ogg Vorbis, Opus, and Bink audio from the URL extension or response content type. An unsupported type, failed download, or failed local save returns `success: false`.

Downloaded clips are saved under the engine's `Saved/AvatarBridge/SpeechFiles/` directory. A successful response means the file was saved and dispatched to the runtime, not that playback or lip sync has finished. `speech/queue-file` dispatches an engine-local `fileName` and does not download or verify the file in the HTTP handler. Oscova normally uses `/tts/` URLs generated by its speech synthesis service.

> **Important:**
> Each layout's yaw uses the engine range `-5.0` through `5.0`. The settings editor displays this as `-45` through `45` degrees. Position, size, and color controls display percentages and send normalized fractions.

## Host proxy differences

Direct engine integrations should use the endpoint table below. The Oscova host proxy uses its own route names for the Blazor UI:

| Host proxy route | Engine route |
| --- | --- |
| `api/avatarbridge/hair?avatarName=Oliver` | `/avatarbridge/hair/list?AvatarName=Oliver` |
| `api/avatarbridge/clothing?avatarName=Oliver` | `/avatarbridge/clothing/list?AvatarName=Oliver` |
| `api/avatarbridge/emotions/set` | `/avatarbridge/emotion/select` |
| `api/avatarbridge/presentation/settings` | `/avatarbridge/presentation/settings` |

The host proxy also rejects `speech/queue` requests unless `url` is an absolute `http` or `https` URL whose path starts with `/tts/`. The engine route itself is documented as accepting an absolute audio URL.

`/avatarbridge/message/display` is available only through the direct Engine API. The Oscova host proxy does not expose a matching route.

## Endpoint reference

| Method | Path | Request | Response |
| --- | --- | --- | --- |
| `GET` | `/avatarbridge/identity` | none | `AvatarIdentityResponse` |
| `POST` | `/avatarbridge/pairing/start` | none | `AvatarPairingStartResponse` |
| `POST` | `/avatarbridge/pairing/complete` | `AvatarPairingCompleteRequest` | `AvatarPairingCompleteResponse` |
| `POST` | `/avatarbridge/activate` | `AvatarActivationRequest` | `AvatarActivationResponse` |
| `GET` | `/avatarbridge/health` | none | `AvatarHealthCheckResponse` |
| `GET` | `/avatarbridge/avatar/list` | none | `AvatarListResponse` |
| `POST` | `/avatarbridge/avatar/select` | `AvatarSelectRequest` | `AvatarSelectResponse` |
| `GET` | `/avatarbridge/hair/list?AvatarName={avatarName}` | none | `AvatarHairListResponse` |
| `POST` | `/avatarbridge/hair/select` | `AvatarHairSelectRequest` | `AvatarHairSelectResponse` |
| `GET` | `/avatarbridge/clothing/list?AvatarName={avatarName}` | none | `AvatarClothingListResponse` |
| `POST` | `/avatarbridge/clothing/select` | `AvatarClothingSelectRequest` | `AvatarClothingSelectResponse` |
| `GET` | `/avatarbridge/emotion/list` | none | `AvatarEmotionListResponse` |
| `POST` | `/avatarbridge/emotion/select` | `AvatarEmotionRequest` | `AvatarEmotionResponse` |
| `POST` | `/avatarbridge/speech/queue` | `AvatarSpeechQueueRequest` | `AvatarSpeechQueueResponse` |
| `POST` | `/avatarbridge/speech/queue-file` | `AvatarSpeechQueueFileRequest` | `AvatarSpeechQueueFileResponse` |
| `POST` | `/avatarbridge/speech/stop` | `AvatarSpeechStopRequest` | `AvatarSpeechStopResponse` |
| `POST` | `/avatarbridge/speech/volume` | `AvatarSpeechVolumeRequest` | `AvatarSpeechVolumeResponse` |
| `POST` | `/avatarbridge/message/text` | `AvatarMessageTextRequest` | `AvatarMessageTextResponse` |
| `POST` | `/avatarbridge/message/display` | `AvatarMessageDisplayRequest` | `AvatarMessageDisplayResponse` |
| `POST` | `/avatarbridge/message/clear` | none or `{}` | `AvatarMessageClearResponse` |
| `POST` | `/avatarbridge/presentation/settings` | `AvatarPresentationSettingsRequest` | `AvatarPresentationSettingsResponse` |
| `POST` | `/avatarbridge/background/color` | `AvatarBackgroundColorRequest` | `AvatarBackgroundColorResponse` |
| `POST` | `/avatarbridge/resolution` | `AvatarResolutionRequest` | `AvatarResolutionResponse` |
| `POST` | `/avatarbridge/graphics/preset` | `AvatarGraphicsPresetRequest` | `AvatarGraphicsPresetResponse` |
| `POST` | `/avatarbridge/shutdown` | `AvatarShutdownRequest` | `AvatarShutdownResponse` |

> **Note:**
> The old byte-stream speech route is not exposed by the host proxy. Use `speech/queue` for URL clips or `speech/queue-file` for engine-local files.

## Request examples

The following examples show JSON field names used by the engine. Most responses include `success` and `description`.

Queue an audio URL:

```json
{ "url": "http://localhost:11317/tts/session/line.wav", "message": "Hello" }
```

Queue an engine-local file:

```json
{ "fileName": "C:\\audio\\line.wav", "message": "Hello" }
```

Stop speech:

```json
{ "forcedStop": false }
```

Keep `forcedStop` set to `false` for the recommended graceful stop. Set it to `true` to interrupt immediately.

The response's `stopped` field echoes `forcedStop`. A successful graceful request therefore returns `stopped: false`. Use `success` to check whether the request was accepted, rather than treating `stopped` as a playback-completion signal.

Set speech volume:

```json
{ "volume": 0.75 }
```

Select an avatar:

```json
{ "avatarName": "<name returned by avatar/list>" }
```

List that avatar's hair options with `GET /avatarbridge/hair/list?AvatarName=Oliver`, then select one of the returned IDs:

```json
{ "avatarName": "Oliver", "hairId": "nomad-wave-undercut" }
```

List that avatar's clothing options with `GET /avatarbridge/clothing/list?AvatarName=Oliver`, then select one returned ID for its slot:

```json
{ "avatarName": "Oliver", "slot": "top", "clothingId": "t-shirt-and-jacket" }
```

Separate tops and bottoms preserve the other separate clothing choice and hair. A configured full outfit is listed in the `top` slot and replaces both visible garments. Selecting a separate top or bottom clears the full outfit. Selecting `default` for both slots restores the avatar's original clothing and body.
Select hair and clothing from the options returned for the target avatar. Unavailable or incompatible options leave the current appearance unchanged. Selection responses report `success: true` when validation, asset loading, and runtime event dispatch succeed. Check the visible result separately. On failure, `description` gives the reason.
Clothing IDs identify garment options, not their current colors. Changing a garment's material color does not change its clothing ID.

Select an emotion:

```json
{ "EmotionName": "<name returned by emotion/list>", "BlendSpeed": 0.25, "Intensity": 0.5 }
```

`BlendSpeed` is the transition duration in seconds. `Intensity` is normalized emotion strength from `0` to `1`. Omit `Intensity` to use `1`, including after a previous request used zero. Explicit `0` removes emotion influence while speech and blinking continue. `0.5` applies half the configured influence, which may not look like half an expression.

Each successful selection applies the supplied intensity, including repeated selection of the same emotion. Success echoes the accepted value as `intensity` in the response. Negative, above-one or non-finite intensity fails before changing the emotion or its strength, with description `Intensity must be a finite number between 0 and 1.` A failure response's `intensity` is `0`, not a report of current scene state. Intensity is scoped to the request and is not persisted as a setting.

Show text without audio:

```json
{ "message": "Text to show without audio" }
```

Show text for a one-shot duration without changing the global display duration:

```json
{
  "message": "Temporary announcement",
  "durationSeconds": 10
}
```

The response echoes `message` and `durationSeconds` with `success` and `description`.

Submit both presentation layouts with `POST /avatarbridge/presentation/settings`:

```json
{
  "Mode": "Auto",
  "DurationSeconds": 5,
  "Subtitle": {
    "Yaw": 0,
    "PositionX": 0.25,
    "PositionY": 0.75,
    "Width": 0.5,
    "Height": 0.2,
    "FontSize": 32,
    "Red": 1,
    "Green": 1,
    "Blue": 1,
    "Alpha": 1
  },
  "Side": {
    "Yaw": 1.5,
    "PositionX": 0.05,
    "PositionY": 0.2,
    "Width": 0.4,
    "Height": 0.6,
    "FontSize": 32,
    "Red": 1,
    "Green": 1,
    "Blue": 1,
    "Alpha": 1
  }
}
```

Every field shown above is required, using the exact field casing. `Mode` must be exactly `Auto`, `Subtitle`, or `Side`. `DurationSeconds` must be a finite, nonnegative number. Each layout has these limits:

| Fields | Requirement |
| --- | --- |
| `Yaw` | Finite number from `-5` through `5`. |
| `PositionX`, `PositionY` | Finite screen fractions from `0` through `1`. |
| `Width`, `Height` | Finite screen fractions greater than `0` and at most `1`. |
| Rectangle extent | `PositionX + Width <= 1` and `PositionY + Height <= 1`. |
| `FontSize` | Integer from `8` through `120`. |
| `Red`, `Green`, `Blue`, `Alpha` | Finite numbers from `0` through `1`. |

To select another mode, submit the complete configuration with `Mode` changed. The Side example requires visual calibration for your avatar and window size. A valid rectangle does not prove that the avatar is within the camera frame.

In Auto, the engine measures the full incoming message against the Subtitle layout's available inner height before revealing its characters. The first overflowing message selects Side. Later messages, including short speech sentences, keep Side until the panel clears explicitly or its display timer expires. Clearing resets Auto to Subtitle. Each new message restarts the existing display timer. Forced Subtitle and Side retain their selected layout after the panel clears. Camera yaw and box position and size animate toward the selected layout.

Successful requests return HTTP `200` with `success: true`, `description: "Presentation settings accepted successfully."`, and `settings` containing the accepted complete configuration. Invalid settings return HTTP `200` with `success: false` and a field-specific `description`, before any presentation update is delivered. Failure `settings` contain neutral defaults and do not report current scene state. The endpoint requires the existing paired bearer token and activation.

> **Note:**
> Request acceptance confirms validation and delivery only. It does not prove completed viewport animation or avatar containment.

Set window and rendering state:

```json
{ "red": 32, "green": 32, "blue": 32 }
```

```json
{ "width": 1920, "height": 1080, "fullscreen": false }
```

Set the quality preset and the independent runtime ray-tracing state:

```json
{ "level": 4, "rayTracingEnabled": false }
```

The response reports the effective state:

```json
{
  "success": true,
  "description": "Graphics preset set to level 4 (Cinematic); ray tracing disabled.",
  "level": 4,
  "presetName": "Cinematic",
  "rayTracingEnabled": false
}
```

Ray tracing defaults to Off, requires supported hardware and rendering configuration, and is independent of `level`. Omitting `rayTracingEnabled` requests Off. If the requested ray-tracing state is unavailable or cannot be applied, the request fails before the quality preset is dispatched. Enabling the master runtime switch does not automatically enable Lumen or another ray-traced effect.

Request shutdown:

```json
{ "forceShutdown": false }
```

## Health and catalog examples

Catalog values depend on the running engine. The names and IDs below illustrate the response shape. Always select from the actual lists returned for your avatar. A successful hair or clothing list can be empty when that avatar has no configured options.

Successful responses expose these fields. Commands include `success` and `description` unless listed otherwise:

| Route | Additional response fields |
| --- | --- |
| `identity` | `success`, `engineId` only |
| `pairing/start` | `expiresInSeconds` |
| `pairing/complete` | `engineId`, `clientId`, `bridgeToken` |
| `activate` | `accessMode` |
| `health` | `success`, `status`, `version`, `port` only |
| `avatar/list`, `emotion/list` | `avatars` or `emotions`, each an array of names |
| `avatar/select`, `emotion/select` | `selectedAvatar`, or `selectedEmotion`, `blendSpeed` and normalized `intensity` |
| `hair/list`, `clothing/list` | `avatarName` and `hairOptions` or `clothingOptions`, as shown below |
| `hair/select` | `selectedAvatar`, `selectedHair` |
| `clothing/select` | `selectedAvatar`, `selectedSlot`, `selectedClothing` |
| `speech/queue` | `savedFilePath` |
| `speech/queue-file` | No additional fields |
| `speech/stop`, `speech/volume` | `stopped` or `volume` |
| `message/text`, `message/display` | `message`, plus `durationSeconds` for `message/display` |
| `message/clear` | `cleared` |
| `presentation/settings` | `settings`, containing accepted mode, duration, and both complete layouts |
| `background/color` | `red`, `green`, `blue` |
| `resolution` | `width`, `height`, `fullscreen` |
| `graphics/preset` | `level`, `presetName`, `rayTracingEnabled` |
| `shutdown` | `shutdownInitiated` |

Health response:

```json
{ "success": true, "status": "running", "version": "1.0.0", "port": 11318 }
```

Avatar list response:

```json
{ "success": true, "description": "ok", "avatars": ["<engine-provided avatar name>"] }
```

Hair list response:

```json
{
  "success": true,
  "description": "Available hair options retrieved successfully.",
  "avatarName": "Oliver",
  "hairOptions": [
    { "hairId": "default", "displayName": "Original" },
    { "hairId": "nomad-wave-undercut", "displayName": "Nomad Wave Undercut" }
  ]
}
```

Clothing list response:

```json
{
  "success": true,
  "description": "Available clothing options retrieved successfully.",
  "avatarName": "Oliver",
  "clothingOptions": [
    { "slot": "bottom", "clothingId": "default", "displayName": "Original Shorts" },
    { "slot": "top", "clothingId": "default", "displayName": "Original T-Shirt" },
    { "slot": "top", "clothingId": "t-shirt-and-jacket", "displayName": "T-Shirt and Jacket" }
  ]
}
```

Emotion list response:

```json
{ "success": true, "description": "ok", "emotions": ["<engine-provided emotion name>"] }
```

Speech queue response:

```json
{ "success": true, "description": "queued", "savedFilePath": "C:\\Avatar\\clips\\line.wav" }
```

## Related articles

- [Bridge Service](references/avatar-engine-bridge-service.md)
- [Speech and Avatar Flow](references/avatar-engine-speech-flow.md)
- [Avatar Settings](references/avatar-engine-settings.md)
