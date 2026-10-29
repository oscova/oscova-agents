# Troubleshooting

Most Avatar Engine issues happen at one of four boundaries: the engine process, the bridge URL, saved-versus-applied settings, or speech clip reachability.

> **Note:**
> Start by separating "Oscova saved the setting" from "the engine applied the setting." Saving only writes configuration. Applying sends a command to the running engine.

## Quick diagnosis

| Symptom | Most likely boundary | First check |
| --- | --- | --- |
| Release is unavailable | Website configuration or release publication | Check your connection to `oscova.ai`, then try again later. |
| Download keeps retrying | Connection interruption or temporary service failure | Leave the command open so it can resume, or press N during the countdown to cancel. |
| Release changed | Publication changed during a retry | Run `oscova avatar install` again. |
| SHA-256 verification fails | Downloaded bytes do not match the published release | Delete no installed files. Run the installation again and report repeated failures. |
| Disk-space check fails | Compressed or expanded files do not fit | Free space on the Avatar Engine destination volume, then retry. |
| Status says **Offline** | Engine process or bridge URL | Start the engine and click **Connect**. |
| Status says **Pairing required** | No usable saved credential | Click **Connect** and enter the code shown by the engine. |
| Status says **Access conflict** | Shared or Exclusive activation conflict | Restart the engine, then activate the intended client first. |
| Status says **Rate limited** | Repeated public pairing calls | Wait for the displayed retry period before selecting **Connect**. |
| Status says **Configuration issue** | Local key or settings storage | Fix `app_data` permissions, then pair with a new code. |
| Status says **Engine error** | Invalid or unexpected engine response | Keep the current engine running, click **Connect** once more, then collect both host and engine logs if it repeats. |
| Status says **Catalog issue** | Engine catalog routes | Check avatar and emotion list support in the engine. |
| **Apply to Live Avatar** is disabled | Unsaved draft or offline engine | Save settings, then refresh the engine. |
| Avatar speaks in browser but not engine | Output mode or `/tts/` reachability | Confirm admin output mode and engine access to Oscova URLs. |
| Message preview does nothing | Message endpoints or offline engine | Test `message/text` and `message/clear`. |
| Lips do not move | Volume or engine lip-sync input | Use a low nonzero speech volume. |

## Installation and download issues

- If installation says current, the published archive matches your local content identity and no archive download is needed.

- A changed release can retain the same version label. Confirm `oscova avatar install` to replace it, or supply `--yes` unattended.

- Stop a running Engine before replacing it. An incomplete installation or unknown version can be repaired through the same command.

- An unavailable check after `oscova avatar start` leaves the Engine running. Retry the check later through installation.

- If interrupted replacement reports a recovery directory, preserve it. Do not delete the only complete Engine copy.

- `oscova avatar install` needs Windows and a working HTTPS connection to `oscova.ai`. It does not need a CLI login.

- If the release is unavailable, check that you can reach `oscova.ai`, then retry later.

- If a transfer is interrupted, leave the command open. Oscova retains its written bytes, shows a five-second countdown, obtains a fresh temporary link, and resumes automatically without an attempt deadline.

- Press N during the countdown or Ctrl+C at any time to cancel. Quiet, JSON, noninteractive, and redirected runs use caller or process cancellation instead of the N choice.

- If the release changes while reconnecting, start a new installation so all metadata and bytes come from one release.

- Validation, permission, and disk failures stop instead of retrying. Follow the reported action before starting again.

- A SHA-256 or size mismatch is rejected before extraction. Repeated mismatches should be reported rather than bypassed.

- Keep enough free space for the compressed ZIP and the expanded engine. Disk usage can change during installation, so extraction can still fail after the advisory check passes.

- Ctrl+C cancels the active download and normally removes files owned by that invocation. If cleanup is blocked, Oscova reports the exact temporary path. A forced process termination can also leave a `.download-<id>` or `.engine-<id>` directory under `~/.oscova/avatar`. After confirming no installation is running, remove only that known temporary directory.

- Use `oscova avatar install --source "C:\Downloads\avatar-engine.zip"` when you need an offline local ZIP. URLs are not accepted through `--source`.

## Connection checks

1. Start the Avatar Engine binary.
2. Confirm **Bridge URL** in Avatar settings, usually `http://127.0.0.1:11318`.
3. Choose **Shared** or **Exclusive**, then click **Connect**.
4. Enter the six-digit engine code if Oscova requests pairing.
   The countdown must move while the dialog is open. If it reaches zero, close the dialog and select **Connect** for a fresh code.
5. Click **Save Settings** if the form has other unsaved changes.
6. Click **Apply to Live Avatar**.
7. If apply fails, check host logs and engine logs.

If the engine runs on another machine, confirm the Oscova host can reach that machine and port. Also confirm the engine machine can reach Oscova's `/tts/` URLs when avatar speech is used.

If you edit **Bridge URL**, the settings screen intentionally clears the previous live status and catalogs. Click **Connect** again before applying avatar or expression choices.

Oscova and Avatar Engine restarts normally reuse the saved pairing automatically. If the engine loses activation, Oscova activates again and reapplies saved runtime settings.

An Exclusive client cannot activate while Shared clients are already active. Restart the Avatar Engine and let the Exclusive client connect first. Shared clients cannot activate while an Exclusive client owns that run.

If Oscova reports a local configuration issue, first run `oscova avatar mcp status` and note its app-data path. Confirm Oscova can read and write `avatar-bridge.key` and `settings/avatar-bridge-credentials.yaml` beneath that effective root. A newly consumed pairing code can replace corrupt local credential state.

To revoke every paired client, stop the Avatar Engine, delete `Saved/AvatarBridge/pairings.json` beside the Engine's saved configuration, and restart it. Every application must pair again. There is no reset button or endpoint.

## Save and apply issues

For [Avatar MCP](references/avatar-engine-mcp-server.md), `oscova avatar mcp status` checks the MCP process, not Engine connectivity. CLI Host and CLI Avatar MCP select the same `.oscova/app_data` root automatically. If a directly started Host asks to pair again, compare its effective `OSCOVA_APP_DATA_DIR` with the path reported by MCP. Both need the same saved URL, encrypted credential, and matching installation key. Restarting MCP does not normally require pairing again. MCP appearance changes are live only and may be replaced by a later web settings apply.

- If **Apply to Live Avatar** is disabled, save the form first and confirm the engine is reachable.
- If saving works but the engine does not change, use the relevant **Apply** button.
- If a section-level apply fails, check whether the engine is running and whether that section's values are valid.
- If **Reset to defaults** seems temporary, remember that it changes only the draft until you save.
- If the next spoken reply reconnects unexpectedly, remember that saving settings invalidates the ready cache by design.
- If a saved avatar or emotion appears as unavailable, refresh against the correct running engine or choose a value from the live list.
- If expression apply fails with a zero **Blend Speed**, increase it above zero. The engine requires a positive value even though the settings slider reaches zero.
- If a full apply fails partway through, earlier settings may already have changed. Fix the reported setting and retry. The apply operation does not undo earlier commands.

## Speech issues

- For automatic chat read-aloud, confirm the output mode includes the avatar and the current user is the operator.
- If queueing fails, confirm the synthesized clip URL is absolute and points under `/tts/`.
- If the engine is remote, confirm it can download the Oscova `/tts/` URL.
- If speech starts but stops early, try **Apply to Live Avatar** and queue a shorter test phrase.
- If text appears but lips do not move, avoid setting **Speech Volume** to exactly `0%`.
- If playback will not stop, disable read-aloud or call the stop action. This sends `speech/stop`.
- If browser-and-avatar timing seems late, remember that Oscova queues the avatar clip after browser playback starts.
- If avatar-only voice mode resumes too soon or too late, check that synthesis responses include accurate clip duration or WAV metadata.
- Avatar speech and chat read-aloud use the voice saved in Speech settings. Save a compatible voice for the selected provider there.
- If `avatar_speak` cannot synthesize, check the operator's selected provider, model, credential, and voice in Speech settings. Unlock stored credentials if needed. No other provider is tried.
- If local-only policy blocks synthesis, choose a local provider endpoint.
- If MCP text speech says the host is stopped, start Oscova Assistant. If login is required, run the reported `oscova login --host` command as the operator.
- If MCP reports a host mismatch, launch Assistant and Avatar MCP with the same `OSCOVA_APP_DATA_DIR`.
- A successful tool response means queue acceptance. Check audible playback separately, and inspect the Engine before retrying a cancelled or lost response.

## API issues

- Use `/avatarbridge/...` routes when calling the engine directly.
- Pair through `identity`, `pairing/start`, and `pairing/complete`, then send the returned bridge token as a bearer credential.
- Call `POST /avatarbridge/activate` with `Shared` or `Exclusive` before command routes in each engine run.
- `401 pairing_required` means the credential is missing or invalid. `409 activation_required` means the paired client must activate again.
- `403 exclusive_access_denied`, `409 exclusive_unavailable`, and `409 access_mode_conflict` report access-mode conflicts.
- Use `api/avatarbridge/...` routes only when calling Oscova's authenticated host proxy.
- Configure font, color, box geometry, camera yaw, and display duration together through `presentation/settings`.
- Expect `speech/queue` through the host proxy to reject URLs that are not absolute `http` or `https` `/tts/` URLs.
- Do not use the removed byte-stream speech route. Use `speech/queue` or `speech/queue-file`.
- Read the JSON `success` and `description` even when the HTTP request succeeds. Command validation and audio download failures can be reported in the body.
- If hair or clothing choices are empty, first obtain an exact avatar name from `avatar/list`. The engine returns only options configured for that avatar. These selections are available through the API and are not saved by Avatar settings.

## Visual issues

- If avatar choices are empty, start the engine and click **Refresh Avatars**.
- If emotion choices are empty, select or load an avatar, then click **Refresh Emotions**.
- If an avatar or emotion does not apply, confirm the selected name exists in the engine list.
- If the window looks wrong, reapply **Window** settings together.
- If message text is off-screen, use **Reset to defaults**, save, then apply the message box.
- If message preview does nothing, confirm the engine supports `message/text` and `message/clear`.
- If a setting applies live but returns after restart, you probably used a section-level **Apply** without **Save Settings**.

## What to collect

Before reporting a bug, collect:

- The bridge URL.
- The exact button or API call that failed.
- Whether **Apply to Live Avatar** succeeds.
- Host logs around `AvatarBridgeController` or `AvatarBridgeService`.
- Engine logs for the matching `/avatarbridge` request.
- Whether speech synthesis produced a `/tts/` URL.
- Whether the failing host response was an HTTP error or a JSON response with `Success = false`.
- Whether the current user is an admin.

## Related articles

- [Connect the Engine](references/avatar-engine-connecting.md)
- [Bridge Service](references/avatar-engine-bridge-service.md)
- [Avatar Settings](references/avatar-engine-settings.md)
- [Speech and Avatar Flow](references/avatar-engine-speech-flow.md)
