# Command Line

The `oscova` command-line tool ships alongside the host. It starts and stops the host, asks agents questions, and inspects agents, threads, tasks, plugins, skills, MCP servers, logs, notifications, tool governance, and the egress ledger.

> **In plain terms:** everything below happens in a terminal, not in this window.

## Sign in separately

- The browser cookie signs you in here and never applies to a terminal.
- `oscova login` creates a CLI token stored in `~/.oscova/auth.json`.
- A host started on this machine writes a control token to `~/.oscova/runtime.json`, so `start`, `stop`, and `restart` work without a login.
- `oscova whoami` shows all three, while `oscova doctor` says what to run when one is missing.

> **Note:** restarting the Host invalidates old browser cookies and CLI tokens. Run `oscova login` again. Browser approval waits for Vault setup or unlock.

## Find your way around

- `oscova --help` lists every command, while `oscova <command> --help` explains one.
- `--json` on any command emits machine-readable output instead of a table.
- `--quiet`, `--no-color`, and `--no-interactive` are accepted everywhere.
- Exit codes: 0 success, 1 internal failure, 2 not signed in, 3 not allowed, 4 usage error.

## Chat from the terminal

Running `oscova` with no command opens a chat session. A dim line above the input shows the host, agent, model, and thread.

- Slash commands inside the session: `/help`, `/status`, `/agents`, `/model`, `/thread`, `/new`, `/clear`, `/quit`.
- Up and Down arrow recall earlier prompts, and Tab completes the highlighted slash command.
- Ctrl+C discards what is typed, and leaves the session from an empty line.
- Tool approvals and requests for input appear as cards you answer in place.

## Everyday commands

- `oscova start --detach` shows preparation progress and optional Avatar setup before leaving the ready Host running.

- **oscova node --host <server-url> --name <device-name>** connects this computer for project work after browser approval. Add **--allow-http** only to accept an unencrypted HTTP connection. Press Ctrl+C to stop foreground operation, or add **--detach** to keep it running after the terminal closes.
- **oscova node status** shows the local node's connection state. **oscova node stop** stops it while preserving enrollment, even if the server is offline. Use the same **--host** when choosing a specific server. Restart the node manually after reboot. Use **--enroll** for fresh enrollment after revocation.
- `oscova stop` waits for the local Host and Avatar MCP to stop, leaving the Avatar Engine running.
- Updating or reinstalling the tool keeps your environment. Startup reports a changed version without asking to reset.
- If database preparation fails, read the terminal's cause and log location before retrying.
- Unsupported database migration history requires a compatible or corrected application release. Reinstalling does not repair missing migrations.
- Use environment recovery only when the failure identifies an interrupted operation.
- `oscova environment erase` previews complete local removal and requires explicit confirmation. It also stops the Avatar Engine.
- `oscova environment reset` retains a recovery copy. Erase permanently removes credentials, Avatar files, and owned environment data.
- `oscova avatar install` resumes interrupted downloads while open. Press N during a retry countdown or Ctrl+C to cancel.
- Avatar installation checks for changed content and confirms replacement. Use `oscova avatar install --yes` for unattended replacement.
- Avatar startup reports an available upgrade. An unavailable check leaves the installed Engine usable.
- `oscova avatar mcp start` exposes Avatar tools locally without starting the Engine or web host.
- `oscova avatar mcp status` and `stop` manage only that MCP service.
- `oscova ask "..."` asks an agent, while `--continue` resumes the last thread.
- `oscova tasks trigger <TASK_ID>` runs a scheduled task now.
- `oscova logs read --level Error` reads host logs.
- `oscova doctor` checks the host connection and credentials.

> **Note:** commands such as `threads delete` ask first. Pass `--yes` to skip their prompt in a script. Under `--no-interactive` without `--yes` they exit 4. `avatar uninstall` instead stops and removes the local engine immediately, without prompting.

## Related

- [Agent Tasks](agent-tasks.md) - the tasks `oscova tasks` triggers.
- [Logs](logs-dialog.md) - the same log files `oscova logs` reads.
