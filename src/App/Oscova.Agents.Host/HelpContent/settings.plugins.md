# Plugins


The plugin list adapts to the available width. Narrow screens show labelled groups while retaining activation and detail controls.

Use Plugins to choose which installed extensions may add capabilities for every user's agents.

> **In plain terms:** a plugin is trusted program code that can run inside Oscova after an administrator enables it.

## Install and review

- Click **Find plugins online** to search NuGet.org for `OscovaPlugin` packages.
- If the plugin is already loaded, Oscova stages the replacement without touching its running files. The new code activates after restart.
- **After restart** shows a pending replacement's name and version, including speech edition changes.
- Changes to a plugin ID's capitalization still update the same installed plugin.
- Finish a pending restart before installing another edition of that plugin.
- Startup installs Agent Templates, Web, Browser, and MXC first, then offers other optional plugins in one checklist. Foundry Local is not offered.
- To add a skipped plugin, install it, enable **Load On Next Restart**, save, and restart.
- Use **Search plugins** to filter the installed list.
- Use the information button to review manifest fields and diagnostics.
- Model-plugin advice separates package requirements, hardware observations and unverified runtime support. **Active** alone does not prove GPU inference.
- LLamaSharp offers base and CUDA 13 editions. Replace the edition, restart, then Test a saved Local provider. CUDA requires a compatible NVIDIA GPU and runtime on Windows or Linux x64.

> **Note:** package validation checks structure, not signatures, reputation, or safety. Enabled plugins run in the Oscova process.

> **Note:** Startup and application updates keep your activation choices and explicitly selected releases, including each speech capability's edition.

## Channel plugins

Discord, Telegram, and Slack are optional plugins. Active channel plugins add their own fields to a shared **Channels** panel. Save channel settings separately from plugin activation. Disabling or uninstalling the plugin removes its channel after restart and preserves its saved configuration.

Google Workspace and Microsoft Graph are optional plugins. When active, they add Google and Microsoft to **Connected Accounts** with their Gmail, Calendar, YouTube, Outlook and To Do tools. Connect each account separately. Removing either plugin keeps the other available after restart.

## Apply changes

- Turn on **Load On Next Restart** for plugins that may run.
- Click **Save Plugin Settings** to store activation choices.
- Click **Reset** to discard unsaved toggle changes.
- Restart Oscova Agents when the restart warning appears.

> **Note:** **Active** means running now. **Load On Next Restart** means allowed after the next restart.

> **Note:** Recognition and synthesis install separately, each with CPU and CUDA editions. CUDA prerequisites must be installed before use.

> **Note:** An incompatible plugin stays installed with its files and settings kept, but cannot be enabled until a compatible release is installed.

## Plugins on project devices

Enabled plugins with compatible project tools prepare automatically on connected project devices, using the same package version and its dependencies. No separate device toggle is needed. Other plugin capabilities continue running on the server.

The plugin row shows which devices are preparing, ready or unavailable. A setup failure sends the operator a notification with **Retry**. Correct the reported problem, then retry. Unavailable tools stay out of device runs until setup succeeds.

Plugin code runs in the node process with its operating-system permissions. **Docker** applies to shell commands and skill scripts, not plugin code.

## Uninstall a plugin

1. Turn off **Load On Next Restart** and click **Save Plugin Settings**.
2. If the plugin is **Active**, restart Oscova Agents and return to Plugins.
3. Click **Uninstall**, then confirm removal.

Uninstall removes the installed package, any staged update, and credentials owned by that plugin across users. Other plugin and Host credentials remain. Other data and settings created by the plugin remain, and an official default is not restored automatically. You can install the plugin again from **Find plugins online**.

## Related

The **Browser** plugin supplies the shared website viewer in chat Workspace. Chromium downloads on first use. Its website profile keeps sign-in cookies and site storage outside Vault-encrypted files, and deletes browsing history and caches when the browser starts and stops. Google sign-in is unsupported in the tested configuration. See [browsing in chat](https://oscova.ai/docs/articles/user-guide/chat-and-agents/main-agent-chat.html#browse-a-website-together) for file transfers and interaction limits.

- [Tools](settings.tools.md) - view capabilities currently available to agents.
- [Find plugins online](dialog.plugin-registry-browser.md) - help for this dialog.
- [Plugin details](dialog.plugin-details.md) - help for this dialog.
