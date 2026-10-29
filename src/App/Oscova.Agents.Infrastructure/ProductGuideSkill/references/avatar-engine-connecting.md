# Connect the Engine

Oscova Agents connects to the Avatar Engine through the engine's HTTP bridge. The default address is `http://127.0.0.1:11318`, which means "this same machine, port 11318."

> **Note:**
> A bridge URL is the address Oscova uses to reach another running program. Use the default for a local engine, or a LAN address when the engine runs on another computer.

## Before you begin

You need:

- The Avatar Engine binary running on the machine that will render the avatar.
- An Oscova account with the `Admin` role.
- Speech synthesis configured if the avatar will speak.
- A bridge URL that the Oscova host can reach.
- For remote engines, network access from the engine machine back to Oscova's `/tts/` URLs.

> **Note:**
> If Oscova and the engine are on the same PC, `127.0.0.1` is correct. If they are on different PCs, `127.0.0.1` points at the wrong machine from at least one side.

## Install and start the engine

On the Windows computer that will render the avatar, install and start the engine through Oscova:

```text
oscova avatar install
oscova avatar start
```

The install command downloads and verifies the server's current release, regardless of the harness version. It does not require `oscova login`. For an offline install, run `oscova avatar install --source "C:\Downloads\avatar-engine.zip"` instead. See the [Command Line](references/user-guide-getting-started-command-line.md) reference for progress, retry, cancellation, and disk-space behavior.

The Engine runs in a separate Windows window. Two monitors or a dedicated Windows renderer PC are recommended when using it fullscreen. Installation leaves it stopped so you can finish Oscova onboarding before starting it.

`oscova avatar start` opens a restored `1280x720` window and listens on `0.0.0.0:11318` for trusted LAN access. On another computer, connect using the renderer's real LAN IP. Use `--address 127.0.0.1` for local access only, or `--port <PORT>` to select a different port.

The operator receives Avatar release notifications automatically, including when the harness computer has no local Engine. Select the notice or **Avatar Engine** in the application update dialog, review the current publication, and confirm **Install Avatar** or **Update Avatar**. On a Windows harness launched through `oscova`, an update stops and restarts a running managed Engine at its previous binding. A stopped or freshly installed Engine stays stopped. Closing the dialog leaves the accepted operation running, and reopening it shows progress or the result. If installation succeeds but restart fails, use the displayed local-start guidance. These actions affect only the harness computer, so install or update a separate Windows renderer on that computer.

For a CLI upgrade, stop the Engine with `oscova avatar stop`, then run `oscova avatar install` and confirm replacement. Current content is not downloaded again. Use `--yes` for unattended replacement. An installation without a release record is shown as unknown and can be replaced after confirmation. Failed download verification or extraction preserves the existing Engine.

Each `oscova avatar start` launches the installed Engine before a brief update check. A failed check does not stop the Engine. Use `oscova avatar status` for local status without a network check. Ordinary Oscova updates preserve Engine files and saved pairing. Complete `oscova environment erase` removes them and requires pairing again after reinstallation.

For an engine installed by Oscova on the same Windows computer:

1. Open **Settings** > **Avatar**.
2. Keep **Bridge URL** on an `http` loopback address such as `http://127.0.0.1:11318`.
3. Select **Start Avatar Engine** beside **Connect**.
4. Wait for the Avatar Engine window to finish opening.
5. Choose an access mode and select **Connect**.
6. Enter the six-digit code shown in the Avatar Engine window.

The start button appears only when the exact managed executable exists and the configured engine is not reachable. It uses the port from the current draft **Bridge URL**, but always binds the local engine to `127.0.0.1`.

For a separately started or remote engine:

1. Start the Avatar Engine with `oscova avatar start`, or start the binary through an equivalent supported deployment.
2. Leave it running while you configure Oscova.
3. Open **Settings** > **Avatar** in Oscova.
4. Confirm **Bridge URL** points at the running engine.
5. Choose an access mode and select **Connect**.
6. Enter the six-digit code shown in the Avatar Engine window.



If the engine is on another machine, use that machine's reachable HTTP address. The Avatar Engine must be able to fetch `/tts/` audio URLs from the Oscova host for speech queue playback.

Pairing is required only once for an Oscova installation. Oscova protects the returned credential locally and reuses it when Oscova or the Avatar Engine restarts.

The pairing dialog keeps the access mode selected on the Settings page and shows the code's remaining lifetime. Select **Pair** once after entering the six digits. If the code expires or the dialog reports a terminal error, close it and select **Connect** for a fresh code. Oscova does not retry an old code automatically.

- **Shared** allows other paired clients to activate in the same engine run.
- **Exclusive** blocks other clients until the Avatar Engine restarts.
- Exclusive activation is rejected when a Shared client is already active. Restart the engine, then let the Exclusive client activate first.

## Understand the status badge

For another assistant, start the independent [Avatar MCP server](references/avatar-engine-mcp-server.md). It shares this screen's saved URL and pairing when both applications use the same app-data directory. Pair through either interface once. MCP restores access internally before protected actions without requiring repeated connect calls.

The Avatar settings screen checks the saved connection silently when it loads and when catalogs refresh. Only **Connect** starts pairing:

- **Not checked** means the bridge URL has not been verified yet, or you edited it after the last check.
- **Checking** means Oscova is connecting and loading live choices.
- **Connected** means the health check passed and live lists were loaded.
- **Offline** means Oscova could not reach the bridge URL.
- **Pairing required** means this Oscova installation needs the engine's six-digit code.
- **Access conflict** means the requested mode conflicts with clients already active in this engine run.
- **Rate limited** means the public pairing service rejected repeated requests temporarily.
- **Configuration issue** means the connection preferences are invalid or Oscova could not save the connection settings or protected credential.
- **Engine error** means the engine answered with an invalid or unexpected response. It is not an offline or local-start condition.
- **Catalog issue** means the engine answered, but avatar or emotion lists could not be loaded.



Editing **Bridge URL** resets the status to not checked and clears live choices until the next connection check.

## Save before applying

The Avatar settings screen separates saved configuration from live engine commands:

- **Save Settings** persists the form values in Oscova.
- **Apply to Live Avatar** connects to the saved bridge URL and pushes the saved visual and audio settings to the running engine.
- Section-level **Apply** buttons send only that section's current draft values to the live engine.

Opening Avatar settings, selecting **Connect**, or refreshing live choices also runs saved-settings synchronization when needed. These actions can restore the saved appearance and audio configuration. Other unsaved fields are not applied by that synchronization.

> **Important:**
> **Apply to Live Avatar** is disabled while there are unsaved changes or while the engine is offline. Save first, then refresh/apply.

Saving can succeed while the Avatar Engine is offline. Applying cannot. This lets you prepare settings before the engine is available, but it also means a successful save is not proof that the engine changed.

## Apply live sections

Use the section buttons when you want immediate feedback:

- **Apply Avatar** loads the selected avatar.
- **Apply Expression** sends the selected emotion and blend speed.
- **Apply Window** sends resolution, fullscreen, graphics preset, and background color.
- **Apply Presentation** sends Auto, Subtitle, or Side mode and both layouts, including camera yaw and message appearance.
- **Apply Audio** sends speech volume.

**Send Preview** shows message text without audio. **Clear** hides the preview panel. **Shutdown Avatar** asks for confirmation before requesting engine shutdown.

**Shutdown Avatar** stops the engine connected at the current Bridge URL through HTTP. The CLI command `oscova avatar stop` remains separate and stops only a process recorded by `oscova avatar start`.

Section-level apply buttons require a reachable engine. **Apply Avatar** also requires that the selected avatar is present in the live avatar list. **Apply Expression** requires that the selected emotion is present in the live emotion list.

## Confirm the connection

A successful **Apply to Live Avatar** call means Oscova connected to the engine and pushed the saved settings. If it fails, check that the engine process is running, the bridge URL is reachable, no firewall blocks the port, and the engine was not claimed by another client.

For speech, also confirm the reverse direction: the Avatar Engine must be able to download the absolute `/tts/` URL Oscova sends. A local engine usually can. A remote engine may need Oscova to be served on a LAN-visible host name or IP address.

## Remote engine checklist

When the Avatar Engine runs on another computer:

1. Start the engine on the remote machine.
2. Find that machine's LAN IP address or DNS name.
3. Enter a bridge URL such as `http://192.168.1.25:11318`.
4. Allow the engine port through the remote machine's firewall.
5. Make sure the remote engine can reach the Oscova host URL used in queued `/tts/` clips.
6. Avoid **Shutdown Avatar** unless you intend to shut down that remote engine.

## Related articles

- [Avatar Settings](references/avatar-engine-settings.md)
- [Bridge Service](references/avatar-engine-bridge-service.md)
- [Troubleshooting](references/avatar-engine-troubleshooting.md)
