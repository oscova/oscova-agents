# Plugins

Plugins add capabilities to Oscova Agents for every user: local speech, model providers, messaging channels, execution environments and agent tools. Only administrators see the Plugins settings page, because an enabled plugin runs trusted code inside the application process.

> **Note:**
> Installing plugin files only makes a plugin visible. Enabling it controls whether it may load after the next restart.



## Automatic and optional plugins

On a clean environment, startup installs Agent Templates, Web, Browser and MXC, in that order. Interactive startup then shows one checklist of the optional plugins that support your platform, all unchecked. Unattended startup installs the four defaults and leaves optional choices for a later interactive start. Unchecked plugins can be installed later through **Find plugins online**.

- Each selected local speech capability offers one CPU or NVIDIA CUDA edition choice on Windows x64 and Linux x64, with CPU initially selected. macOS uses CPU. Install NVIDIA prerequisites separately.
- **Oscova OpenAI** (`Oscova.Agents.Plugins.OpenAI`) supplies hosted speech recognition and synthesis together. Enable it and restart before choosing OpenAI in Speech settings. Both can reuse the saved OpenAI credential used by model configurations. OpenAI model support works without this plugin.
- **Oscova ElevenLabs** (`Oscova.Agents.Plugins.ElevenLabs`) supplies hosted speech recognition and synthesis with one user-owned API-key credential. Enable it and restart, then select the credential and an account voice in [Speech settings](references/user-guide-getting-started-voice.md). Both directions use your ElevenLabs credits.
- Discord, Telegram and Slack are optional channel plugins. Each adds its own page under **Settings > Channels**, saved separately from activation. Disabling or uninstalling one removes its page after restart and keeps its saved configuration.
- Browser is installed by default and supplies the managed browser in the chat Workspace. Chromium starts on demand and downloads on first use. See [browsing in chat](references/user-guide-chat-and-agents-main-agent-chat.md).
- The optional Docker plugin adds an environment and operator image panel under [Execution](references/user-guide-settings-and-admin-execution.md) and supplies containerized STDIO launching. Install and enable it, then restart.
- Foundry Local is excluded from startup setup.

Installed defaults keep your activation choice and selected edition. A package you selected explicitly through an install or update is preserved on later starts, including a newer compatible release. If that choice becomes incompatible, Oscova tries the reviewed release for the same official package edition. A plugin without a compatible release is disabled before it executes, keeps its files, settings, models and data, and its row explains why it cannot load.

Startup shows selected package rows with progress, processed counts and a final result. `--quiet` and `--json` suppress this feedback. Failed installations continue startup and keep diagnostic details in application logs.

After a direct .NET tool update, the next startup checks for newer compatible releases of installed plugins. Interactive startup asks before applying each, with **No** as the default, and unattended startup reports the choices. During a UI application update, select plugin releases in the update dialog before restarting. The completed result appears after sign-in and Vault unlock, with **Open Plugins** when a preserved plugin needs attention.

## Plugins on project devices

After an enabled plugin loads on the server, its compatible project tools prepare automatically on connected project devices with the same package version and runtime dependencies. The activation choice controls availability, with no separate device toggle.

- The installed plugin row lists devices that are preparing, ready or unavailable.
- Setup problems create an operator notification with a **Retry** action. Correct the problem, then retry. Tools stay unavailable on that device until preparation succeeds.
- Only toolsets whose author declares node support run this way. Server integrations, credentials and the operator's private files are not copied to user devices.
- Native plugin code runs with the node process's operating-system permissions. Selecting **Docker** isolates shell commands and skill scripts, not native plugin code.

## Install plugins online

Model plugins can declare hardware requirements. The catalog, installed row and Details show these separately from detected hardware and **Active** or restart status. A detected GPU does not prove that native runtime libraries load. Unknown requirements or an unavailable check still allow an explicit installation choice. Catalog advice reads package metadata without downloading native payloads.

LLamaSharp offers base and NVIDIA CUDA editions with the same plugin identity. Base uses CPU on Windows, Linux and Intel macOS, and Metal on Apple Silicon. CUDA targets Windows and Linux x64 with CUDA 13. Use the existing replacement flow and restart, then run **Test** on a saved Local provider to verify inference and any observed offload. Foundry Local selects its own runtime, so its placement remains managed.

1. Select **Find plugins online**, enter a package name or search term and select **Search**. Oscova searches NuGet.org for packages with the `OscovaPlugin` package type.
2. Review the publisher, project, license and package in **Details**. A **Verified** badge appears only when NuGet marks the result verified. Neither the badge nor Oscova's validation proves the code is safe.
3. Select **Install or update**. The button shows **Installing...** while Oscova downloads and validates the package, then **Installed** when it is ready. If the install fails, the error appears below the row and the button changes to **Retry**.
4. Enable the new **Inactive** plugin and restart Oscova Agents.

What Oscova accepts:

- The newest stable release whose declared framework range supports this Oscova version. Releases with missing, malformed or incompatible ranges are skipped. An incompatible result stays listed with the button off and the reason beside its name: **Requires a newer Oscova**, **Not compatible with this Oscova** or **Prerelease only**.
- The expected plugin package type, a root `oscova-plugin.json` manifest and a declared plugin assembly selected for .NET 10. The installed manifest must agree with the published framework dependency range.
- A package of 25 MB or smaller. Ordinary managed, native and resource dependencies for the current operating system and processor are restored separately, and NuGet may temporarily download much more than the final layout needs.

Plugins that do not support your platform can still appear in the online results. Selecting **Install or update** shows **This plugin is not available on this platform.** before downloading its dependencies.

If installation fails, Oscova removes the partial staging directory. After a successful install, the plugin loads from its installed files and no longer needs the feed.

## Update an installed plugin

1. Find the package again through **Find plugins online** and select **Install or update**. Oscova stages the newest compatible stable version separately while the current plugin keeps running.
2. Confirm **Load On Next Restart**, save any activation changes and restart. Startup applies the staged replacement before loading plugins and keeps your activation choice.

- Only one pending install or update per plugin is allowed. The row shows **After restart** with the incoming name and version, including same-version package editions.
- A change in a plugin ID's capitalization still updates the same plugin.
- If a different package claims an installed plugin ID, Oscova shows both identities and offers **Replace plugin**. Confirm only when you intend to change the package owner. A pending-update conflict has no replacement action, so finish the pending restart first.
- If a valid staged replacement cannot be moved into place, Oscova restores the previous installation and retries on a later startup. An invalid staged package is discarded. Check application logs and download again if the expected version does not appear.

## Install local speech and switch editions

Recognition and synthesis are separate optional capabilities. Install `Oscova.Agents.Plugins.LocalSpeechRecognition` for Whisper recognition, `Oscova.Agents.Plugins.LocalSpeechSynthesis` for Kokoro synthesis, or both. Recognition supports Intel and Apple Silicon macOS. Synthesis on macOS requires Apple Silicon.

For NVIDIA acceleration on Windows x64 or Linux x64:

1. Prepare the [CUDA prerequisites](references/user-guide-getting-started-voice.md).
2. Search for `Oscova.Agents.Plugins.LocalSpeechRecognition.Cuda` or `Oscova.Agents.Plugins.LocalSpeechSynthesis.Cuda` and select **Install or update**.
3. Confirm **Load On Next Restart** when needed, save and restart the host completely.

Each capability's CPU and CUDA editions share one plugin ID, so activation, models and voice settings are kept. To return to CPU, install the package without `.Cuda` and restart. Finish a pending replacement before selecting another edition. Native libraries cannot switch inside a running process, and a CUDA initialization failure does not roll back the edition. Dependency downloads, model files, extraction space and NVIDIA prerequisites are separate from the small plugin archive.

## Enable a plugin

1. Review the plugin row and its details dialog.
2. Turn on **Load On Next Restart** for plugins you trust.
3. Select **Save Plugin Settings**.
4. Restart Oscova Agents when the warning appears.

**Reset** discards unsaved toggle changes. **Search plugins** filters the installed list by name, ID or version.

## Uninstall a plugin

1. Turn off **Load On Next Restart** and select **Save Plugin Settings**.
2. If the plugin is **Active**, restart Oscova Agents and return to Plugins.
3. Select **Uninstall** and confirm.

> **Note:**
> Uninstall removes the installed package, any staged update and that plugin's credentials and connected-account tokens for all users. Other credentials and the data and settings the plugin created are kept. An uninstalled default is not restored automatically. Install it again from **Find plugins online**.

## Statuses and details

- **Inactive**: installed but not running in this session.
- **Active**: loaded when the current host process started. This confirms loading, not that native GPU inference is ready.
- **Load failed**: enabled at startup but could not load.
- **Invalid manifest**: the `oscova-plugin.json` or referenced assembly failed validation.

The restart warning appears when the saved allowlist differs from startup or a validated replacement is pending. The information button shows the name, plugin ID, version, assembly, entry type, current status, next-start setting and any diagnostic message. Diagnostics explain failures and are not a security review.

## Troubleshooting

- **No installed plugins were found:** confirm plugin folders are under the configured `app_data/plugins` directory.
- **A plugin stays inactive after enabling:** restart Oscova Agents after saving.
- **Save Plugin Settings fails:** only installed plugins with valid manifests can be enabled.
- **Online install reports a conflict:** an install or update for that plugin is in progress or awaiting restart. Restart first.
- **Online install says the package is unsupported:** confirm it declares `OscovaPlugin`, includes a root `oscova-plugin.json` and exposes the declared assembly as a .NET 10 runtime asset.
- **A dependency cannot be restored:** confirm every package dependency and current-platform runtime asset is on NuGet.org. Plugin-to-plugin dependencies are not supported.
- **Online install says the package is too large:** the package exceeds the 25 MB limit.
- **Online search fails:** NuGet.org could not be reached or returned an upstream error.

## Related articles

- [Build your first plugin](references/developer-guide-plugins-first-plugin.md)
- [Plugin manifest](references/developer-guide-plugins-manifest.md)
- [Included tools](references/user-guide-concepts-included-tools.md)
- [Tool governance](references/user-guide-concepts-tool-governance.md)
- [Execution](references/user-guide-settings-and-admin-execution.md)
