# Application update

Use **Check for updates** in the navigation rail to inspect your installation. The dialog opens at once and shows **Checking for updates...** until the check ends. A newer stable release of Oscova or an installed plugin changes the action to **Update available**. Previews are never offered. **Refresh** checks again when the release check is unavailable.

Harness and Avatar Engine availability checks run automatically at startup and every six hours. Saved notifications open the matching update flow. **Refresh** checks both sources immediately. Select **Avatar Engine** to review installation or updates on this computer.

The plugin table lists every installed plugin with a newer compatible stable release, with installed and available versions. All listed plugin updates are installed together with the update. When only plugins have updates, the update keeps the installed Oscova version and replaces those plugins.

**Read release notes** opens the available release on GitHub. **All release notes** opens the complete history. Help also provides a **Release notes** link.

**Update and restart** opens a console on the Host computer that runs `oscova update`. Finish important work first because Oscova is unavailable during the update. The console shows each step, reports the healthy Host and closes. Closing it early does not stop the update or the Host. If the Host computer has no desktop terminal, the update runs in the background and `oscova update status` shows its progress.

The update stops the Host and Avatar MCP, keeps the Avatar Engine running, and saves an encrypted recovery checkpoint before it changes your environment. Restart MCP afterward with `oscova avatar mcp start` if needed.

After a successful restart, the result opens automatically when the operator signs in and unlocks the vault. It lists only what changed. **See what's new** opens the installed release notes when available for that version. **Close** acknowledges the result. **Open Plugins** appears when a plugin needs attention.

Incompatible plugins are disabled with their files, settings, and data kept. Update them from Plugins when a compatible release is available, then enable them again.

Closing the dialog or browser does not cancel an accepted update. Large downloads can take several minutes. The browser keeps waiting after its longer-wait notice, and returning to the page resumes checking. If Oscova is still running two minutes after you confirm, the update did not start: the page reloads and reports that nothing changed. An unresolved operation prevents another update.

The automatic path requires a standard global-tool installation, the default app-data database, and control over application startup. Stop or disable any external supervisor before confirming.

If the update fails before it changes your environment, Oscova stays on the current version and the dialog shows the reason and **Retry**. If it fails later, the console stays open with the previous version, the checkpoint, recovery-key and log paths, and offers to go back to the previous version. From any terminal on the Host computer, `oscova update --version <PREVIOUS_VERSION>` does the same. Confirm the restore question, and that version restores the checkpoint at the original data path and reinstalls its plugins. `oscova update status` and the saved `recovery.txt` show the same instructions.

External workspaces, local models, voices, and system logs remain outside the checkpoint and stay in place.

## Related

- [Avatar Engine installation and updates](avatar-update.md)
