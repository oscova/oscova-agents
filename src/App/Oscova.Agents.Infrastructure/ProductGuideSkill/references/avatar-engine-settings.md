# Avatar Settings

Avatar settings control how Oscova Agents reaches the separate Avatar Engine and what state it applies to that running engine.

> **Note:**
> The Avatar Engine is not part of the Blazor web host. Oscova saves settings locally, then sends HTTP commands to the engine when you apply them.

Avatar settings are shared system settings. They are not personal preferences for one user. The screen is available to the operator because it controls the shared avatar runtime and can send commands to a separate process.

## Save and apply behavior

The page uses a draft pattern:

- Editing a field changes only the on-screen draft.
- **Save Settings** writes the draft to Oscova's shared avatar settings.
- Saving marks the live runtime as not ready, so the next speech sequence or **Apply to Live Avatar** reconnects and reapplies the saved snapshot.
- **Apply to Live Avatar** is available only when there are no unsaved changes and the engine is reachable.
- **Reset to defaults** replaces the draft with default values but does not save them.

Section-level **Apply** buttons send current draft values to the live engine immediately. They do not replace **Save Settings** when you want the values kept for later sessions.

> **Important:**
> A section-level **Apply** can change the live engine with unsaved draft values. Save afterward if you want those values to survive a restart or the next runtime sync.

The full saved-settings apply sends values in this order: resolution, graphics preset, background color, speech volume, selected avatar, optional hair, optional top, both presentation layouts and their selected mode, then selected emotion.

## Connection

**Bridge URL** is the engine address. The default is `http://127.0.0.1:11318`. A blank saved value falls back to the default.

**Access mode** controls who may activate during one Avatar Engine run. Shared permits other paired clients. Exclusive blocks them until the engine restarts. An Exclusive request cannot displace clients that already activated in Shared mode.

**Connect** saves only **Bridge URL** and **Access mode**, leaving other unsaved Avatar fields unchanged. It then checks the saved pairing. If pairing is required, Oscova asks for the six-digit code shown in the Avatar Engine window. The dialog displays the saved mode without editing it and counts down to the code's expiry. An expired code requires a fresh **Connect**.



Connecting, opening this screen, and refreshing live choices also synchronize the saved settings when needed. They can therefore change the running engine even if you have not selected **Apply to Live Avatar**. Unsaved visual fields remain draft values.

On Windows, **Start Avatar Engine** appears beside **Connect** only when Oscova finds the exact managed executable at `~/.oscova/avatar/engine/OscovaAvatarEngine.exe` and the configured engine is offline. Pairing, access conflicts, rate limits, and local configuration errors do not offer another local start.

The operator can install the server's current Engine from its release notification or **Avatar Engine** in the application update dialog. After installation completes, this screen refreshes local installation availability. A harness launched through `oscova` tracks local starts so an Avatar update can restart a previously running Engine. Saved connection and appearance settings remain available. Updating affects this computer only, even when **Bridge URL** points at another renderer. See [Connect the Engine](references/avatar-engine-connecting.md) for installation and update steps.

Local start accepts only an `http` Bridge URL whose host is `localhost` or `127.0.0.1`. Oscova uses the URL's effective port and binds the local Engine to `0.0.0.0` for trusted LAN access. The client Bridge URL remains a reachable address such as `http://127.0.0.1:11318` on the same computer.

After **Start Avatar Engine** succeeds, wait for the Avatar Engine window to finish opening, then select **Connect**. Oscova does not poll startup readiness. The start button stays hidden until a connection check reports that the engine is still offline, which then permits a retry.

> **Note:**
> `oscova avatar start` listens on all interfaces by default. Connect through the renderer's real LAN address. For access from the renderer alone, use `oscova avatar start --address 127.0.0.1`.

Use **Apply to Live Avatar** after starting or restarting the engine. The apply pass connects, applies window, graphics, background, message, camera, audio, avatar, and emotion settings, then reloads the avatar and emotion lists.

The status badge is based on a live refresh:

- **Connected** means the bridge health check and catalog load succeeded.
- **Offline** means the bridge URL could not be reached.
- **Pairing required** means **Connect** must complete the six-digit pairing flow.
- **Access conflict** means the requested Shared or Exclusive mode is unavailable for this engine run.
- **Rate limited** means you must wait before selecting **Connect** again.
- **Configuration issue** means the connection preferences are invalid or Oscova could not save the settings or protected credential.
- **Engine error** means the engine returned an invalid or unexpected response. Starting another local engine will not fix it.
- **Catalog issue** means health succeeded but avatar or emotion choices failed to load.
- **Not checked** means the bridge URL has changed or has not been refreshed.

Ordinary Oscova and Avatar Engine restarts do not require pairing again. Oscova reactivates the saved client after an engine restart and reapplies the complete saved settings snapshot.

## Avatar and expression

**Refresh Avatars** calls the running engine for available avatar names. **Apply Avatar** is disabled until a non-empty avatar is selected.

**Refresh Emotions** calls the running engine for available emotion names. **Apply Expression** is disabled until a non-empty emotion is selected. **Blend Speed** ranges from `0` through `5` in the UI and is sent with the selected emotion.

**Cue Blend-In Seconds** ranges from `0.1` through `2` and defaults to `0.5`. **Cue Restore After Seconds** ranges from `1` through `10` and defaults to `3`. These values control emotion cues in spoken chat replies. Save them with **Save Settings**. **Apply Expression** does not send them to the engine. The existing **Blend Speed** controls the return to the selected emotion.

Choose a blend speed greater than zero. Although the slider reaches zero, the engine rejects zero when applying an expression.

Avatar and emotion names are case-sensitive at the engine boundary.

After selecting an avatar, choose its **Hair** and **Top** from the live Engine lists. The lists show display names, while Oscova saves the Engine's exact IDs. Top shows only clothing options in the `top` slot. An empty choice sends no hair or top override; an Engine option named `default` is a real choice.

**Apply Appearance** previews the draft by selecting the avatar, then hair, then top. **Save Settings** stores those choices for later Engine sessions. Changing the avatar clears the draft hair and top choices. Saving by itself does not immediately preview the change.



If a saved avatar, hair, top, or emotion is absent from the running Engine catalog, its value remains visible as unverified or unavailable until you replace it. **Apply Appearance** requires a live selected avatar and reports a rejected hair or top choice without hiding the avatar. A rejected saved hair or top choice is logged during background synchronization without blocking avatar speech.

## Window and camera

**Resolution** offers the presets shown in the UI, from `1280x720` through `5120x2160`. **Graphics Preset** maps `Low`, `Medium`, `High`, `Epic`, and `Cinematic` to engine levels `0` through `4`.

**Ray Tracing** is independent of the graphics preset. It defaults to Off and requires compatible hardware, drivers, and rendering configuration. Turning it on makes supported ray-traced effects available and can reduce performance. It does not automatically enable Lumen or add ray-traced effects that the Avatar Engine project has not configured.

**Fullscreen**, **Ray Tracing**, and **Background Color** are applied with the same **Apply Window** action.

**Camera Yaw** is shown as `-45` to `45` degrees in Oscova. The engine receives the mapped yaw value from `-5.0` through `5.0`.

Default window settings are `1280x720`, windowed mode, graphics preset `Medium`, ray tracing Off, and RGB background `134,134,134`. CLI and Settings launches request a restored window with a title bar, initially positioned at `100,100`.

## Message panel

The message panel can show speech text, captions, or a preview without audio.

- Each **Subtitle** and **Side** layout has its own font size (`8` through `120`), color, opacity, camera yaw, and message rectangle.
- **Edit layout** opens an aspect-correct preview of the selected Avatar screen resolution.
- Drag the message box to move it, resize it from any corner, or enter exact **Left**, **Top**, **Width**, and **Height** percentages.
- **Left** measures from the screen's left edge and **Top** measures from its top edge. The entire box must remain inside the screen, and the visual editor requires width and height of at least `1%`.
- **Save** in a layout editor updates that preset in the page draft. **Test Layout** temporarily applies the chosen preset, shows the preview message for the configured duration, then restores Auto, Subtitle, or Side. **End Test** clears the message and restores the selected mode immediately. **Apply Presentation** sends both layouts and the selected mode to the running engine. **Save Settings** persists them for later sessions.
- **Display Duration** is in seconds. `0` disables ordinary message display.
- **Send Preview** calls `message/text`. **Clear** calls `message/clear`.

Subtitle starts with yaw `0`, position `25%` from the left and `75%` from the top, and size `50%` wide by `20%` high. Side starts with yaw `-23` degrees, Left `6.5%`, Top `25%`, width `41.5%`, and height `45%`. Both use font size `32` and white text at full opacity. Visually check avatar framing at your supported window sizes. The engine does not measure avatar containment. Display duration defaults to `5` seconds and is shared by both layouts.

The settings UI displays box values as percentages, but the engine receives fractions. For example, `25%` is sent as `0.25`.

## Audio and shutdown

Avatar speech uses the operator's selected provider, model, endpoint, credential, and voice from **Speech** settings. This applies to built-in `avatar__speak` and MCP `avatar_speak`. Save voice changes in Speech settings to use them on the next request.

**Speech Volume** ranges from `0%` through `100%`. The engine may not drive lip sync when volume is exactly zero, so use a low nonzero value when you want very quiet speech with visible mouth movement.

**Shutdown Avatar** asks for confirmation, then sends a shutdown request to the engine at the current bridge URL. This can affect an engine running on another machine if the bridge URL points there.

The Settings start action and the `oscova avatar` CLI lifecycle are separate. A UI-started engine is not recorded by `oscova avatar status` and is not stopped by `oscova avatar stop`. After connecting, use **Shutdown Avatar**. If the engine never becomes reachable, close its window.

The Audio section keeps **Speech Volume** available independently of synthesis plugins. Install and configure a synthesis provider in Speech settings before requesting avatar speech.

## Default values

| Setting | Default |
| --- | --- |
| Bridge URL | `http://127.0.0.1:11318` |
| Access mode | Shared |
| Emotion blend speed | `1.0` |
| Cue blend-in | `0.5` seconds |
| Cue restore after | `3` seconds |
| Background color | RGB `134,134,134` |
| Resolution | `1280x720`, windowed |
| Graphics preset | `Medium` (`1`) |
| Ray tracing | Off |
| Subtitle | Yaw `0`, font size `32`, white RGBA, Left `25%`, Top `75%`, width `50%`, height `20%` |
| Side | Yaw `-23` degrees, font size `32`, white RGBA, Left `6.5%`, Top `25%`, width `41.5%`, height `45%` |
| Message display duration | `5` seconds |
| Presentation mode | Subtitle |
| Speech volume | `100%` |

## Related articles

- [Connect the Engine](references/avatar-engine-connecting.md)
- [Bridge Service](references/avatar-engine-bridge-service.md)
- [Speech and Avatar Flow](references/avatar-engine-speech-flow.md)
