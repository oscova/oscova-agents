# Avatar Engine

Use this screen to save Avatar Engine settings in Oscova and apply them to the running engine.

> **In plain terms:** pair once, then Oscova reconnects after either program restarts.

## Connect and pair

- **Bridge URL** defaults to `http://127.0.0.1:11318`.
- **Shared** allows other paired clients.
- **Exclusive** blocks other clients until the engine restarts.
- Select **Connect** to save only the URL and access mode, then connect.
- **Start Avatar Engine** appears only when an installed local engine is offline.

> **Note:** Restarting Oscova or the engine does not require another code unless pairing was reset.

> **Note:** Avatar MCP shares this pairing and saved connection when both use the same app-data directory.

## Save and apply

- Select **Save Settings** before **Apply to Live Avatar**.
- Section **Apply** buttons send only that section's draft values.
- Select an avatar to load its available **Hair** and **Top** choices from the running engine. The names shown in the lists are the engine's display names.
- Select **Apply Appearance** to preview the avatar, hair, and top now. Select **Save Settings** to restore those choices on later engine sessions.
- Changing the avatar clears the draft hair and top choices. A saved choice that the engine cannot verify stays visible so you can replace it deliberately.
- Sliders have a compact number field beside them for exact values.

> **Note:** **Apply to Live Avatar** is disabled while the form has unsaved changes or the engine is offline.

## Place the message box

- Select **Edit layout** to see the message box within the selected Avatar screen resolution.
- **Apply Message Box** sends the draft position, size, and duration to the running engine.
- **Save Settings** keeps the draft for later sessions.

## Tune the avatar

- **Graphics Preset** supports Low through Cinematic.
- **Ray Tracing** is separate, requires compatible hardware, and can reduce performance.
- Avatar tools use the operator's provider and voice saved in **Speech** settings. **Speech Volume** controls renderer volume independently.
- Text speech through Avatar MCP also requires the running Assistant host and an operator CLI login.
- Keep **Speech Volume** above zero for visible lip sync.
- **Cue Blend-In Seconds** sets how quickly a spoken reply's emotion appears, from `0.1` to `2` seconds.
- **Cue Restore After Seconds** sets how long it stays before returning, from `1` to `10` seconds.
- **Blend Speed** controls the return to the selected emotion. Save these timing values with **Save Settings**.

- **Reset to defaults** changes the draft only.
- Select **Save Settings** to keep reset values.
- **Shutdown Avatar** asks before closing the engine at the current bridge URL.

## Related

- [Agents](settings.agents.md) - agents whose replies can be spoken through the avatar.
- [Pair Avatar Engine](dialog.pair-avatar-engine.md) - help for this dialog.
- [Message layout](dialog.avatar-message-layout.md) - help for this dialog.
