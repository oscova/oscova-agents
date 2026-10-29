# Execution

The operator chooses where shell commands and bundled skill scripts run for every account.

- Open **Settings > Agents & AI > Execution**.
- **Host** is the default. Commands use the operating-system account running Oscova, or the selected project's device account.
- **Disabled** prevents commands and bundled scripts from running.
- Other environments appear when their plugins are installed, enabled, and admitted after restart.
- Select an **Execution environment** and wait for its automatic check. **Save** is blocked while checking or unavailable. Reselect it after correcting setup to check again.
- **Microsoft MXC** is installed by default and applies existing workspace and network permissions through native operating-system isolation. Supported native dependencies must be available on the executing device.
- Start a new run after saving. Active affected work requires interruption confirmation.
- An unavailable saved environment is retained. Enable its plugin and restart, or select and save another environment.
- Scripts require a runtime and invocation supported by the selected environment.

> **Note:** Host commands cannot guarantee no network access, so Offline and agent-local-only runs reject them. Full access preserves Disabled and otherwise selects Host for that conversation.

> **Note:** Selecting Docker shows its image panel immediately. MXC shows its own information panel. Host and Disabled show no plugin panel. Docker is optional during installation. Each device checks its own prerequisites.

## Related

- [Skills](settings.skills.md): find, import, share, and remove skills.
- [Plugins](settings.plugins.md): install and enable optional environments.
