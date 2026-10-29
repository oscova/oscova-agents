# Command line

The `oscova` command-line tool installs and runs Oscova Agents, asks agents questions from a terminal and inspects most of what the web UI shows: agents, threads, tasks, plugins, skills, MCP servers, logs, notifications, tool governance and the Egress Audit ledger.

Run `oscova --help` for the command list, `oscova <command> --help` for one command, and `oscova doctor` when something is not working.

## Install

Run the command for your operating system in the named terminal, as a normal user without an elevated terminal or `sudo`.

**Windows x64, PowerShell:**

```powershell
irm https://oscova.ai/install.ps1 | iex
```

**macOS Intel or Apple Silicon, Terminal:**

```bash
curl -fsSL https://oscova.ai/install.sh | bash
```

**Linux x64 with glibc, Terminal:**

```bash
curl -fsSL https://oscova.ai/install.sh | bash
```

Requirements:

- Windows installation is x64 only.
- macOS needs version 15 or newer and a native Intel x64 or Apple Silicon terminal.
- Automatic SDK setup on Linux supports Ubuntu 24.04 x64. On another glibc x64 distribution, install the .NET 10 SDK with Microsoft's Linux instructions first.
- ARM Linux and musl Linux are unsupported.

What the installer does:

1. Checks for a stable .NET 10 SDK and both .NET 10 runtimes. If they are missing, it asks before installing the SDK through your system's normal installer. This needs an interactive terminal and may request administrator approval for the SDK only.
2. Installs the latest stable `Oscova.Agents.App` release from NuGet.org for the account running the command. A second OS account can share the system SDK but installs the tool separately.
3. Verifies the tool's `--version` and `--help` commands.
4. Runs `oscova start --detach`, so Oscova keeps running after you close the terminal. Open the address it prints to complete setup. Without an interactive terminal, it prints `oscova start` for you to run instead.

Rerunning the installer verifies an existing installation and reports its version. It never updates or replaces it. See Update the installed application.

### Choose plugins on first start

On the first interactive start, Oscova installs Agent Templates, Web, Browser and Microsoft MXC, then shows a checklist of the remaining optional plugins, all unchecked. Use the arrow keys to move, Space to toggle and Enter to continue, including with nothing selected.

- Each selected local speech capability then offers **CPU** or **NVIDIA CUDA** on Windows x64 and Linux x64, with CPU selected initially. Recognition and synthesis can use different editions. macOS uses CPU without asking. NVIDIA runtime prerequisites are installed separately.
- Foundry Local is not offered here because its native dependencies can conflict with other local runtimes.
- Cancelling before all choices finish records no installation or decline.
- Unchecked plugins can be installed later through **Settings > Plugins > Find plugins online**.
- On Windows, Oscova then offers optional Avatar Engine installation from the current server publication. Declining continues setup. macOS and Linux show a note to run `oscova avatar install` on a Windows renderer computer instead.
- Foreground and detached startup show the same checklist. Unattended, quiet, JSON and redirected-input startup offer no optional choices and keep installed editions and activation choices.

### Install as a Chrome app

1. Run `oscova start` and open its localhost address in Chrome on the same computer.
2. Select Chrome's install button in the address bar when it appears, then confirm.
3. Open Oscova from the installed app shortcut. Keep the Oscova server running while you use it.

Chrome opens the app in its own window with the normal sign-in, Vault unlock and logout. HTTP localhost supports installation without certificate setup. A plain HTTP LAN address is not a secure context, so for other devices use HTTPS as described in [Network](references/user-guide-settings-and-admin-network.md).

Keep using the same address and port. `localhost`, `127.0.0.1` and the Tailscale hostname are separate browser origins with separate installations and sign-in state. If the address or port changes, install again from the new address. After an application update, reopen the app window once the server restarts.

### Manual installation and recovery

You can read the PowerShell script or the Bash script before running it. To inspect it first, save it, read it, then run `powershell -File .\install.ps1` or `bash ./install.sh` from its directory. The scripts do not support unattended consent.

If you declined the prerequisite, cancelled the administrator prompt or SDK setup failed:

1. Install the .NET 10 SDK with Microsoft's instructions. A runtime alone is not enough, because a global tool needs the SDK.
2. Rerun the install command, or install the package directly with `dotnet tool install --global Oscova.Agents.App`. Investigate an incomplete installation from the reported NuGet error before retrying.
3. If a Unix terminal cannot find `oscova` after a successful install, run `export PATH="$HOME/.dotnet/tools:$PATH"` and add the same line to your shell startup file. A new terminal alone does not add the entry.

The tool lives in `%USERPROFILE%\.dotnet\tools` on Windows or `$HOME/.dotnet/tools` on macOS and Linux. The scripts use WinGet or Ubuntu's configured apt sources when available. On macOS, and on Windows without WinGet, they verify the SHA-512 hash of a pinned official .NET 10 SDK installer before running it. That SDK download is serviced separately from Oscova releases.

### When database preparation fails

`oscova start` and `oscova start --detach` show the failure cause and the system log location. Read those logs when the Host cannot start.

- **Unsupported migration history** means Oscova refused to apply database migrations. Use an application release compatible with that database, or a corrected release with the required migrations. Updating or reinstalling preserves data but does not repair missing migrations. A reported version change does not mean preparation succeeded.
- **An interrupted operation** is the only case for environment recovery. For an interrupted application update, run `oscova update status` and follow its recovery instructions. Otherwise run the recovery command shown for that environment operation. Ordinary database failures create no recovery copy.

## Where the CLI keeps your environment

A CLI-managed installation keeps its database, users, agents, settings, files, plugins, credentials and logs in `.oscova/app_data` under your user profile. Updating or uninstalling the .NET tool leaves this directory in place.

- `OSCOVA_APP_DATA_DIR` selects a different environment directory when set to a nonblank absolute or relative path.
- A Host started directly from a development or published application defaults to `app_data` beside that application. Set `OSCOVA_APP_DATA_DIR` when it should share the CLI's environment.
- Keeping the directory does not make an older environment compatible with a release that uses a different data format. Create an encrypted backup before an update and check the release notes.

### Transfer an existing package-local environment

If an earlier global-tool installation still holds `app_data/app.db` or an `app_data/users` directory inside the installed package, a local CLI Host refuses to start until you transfer it. Do this before `dotnet tool update`, `dotnet tool uninstall` or deleting the old package:

1. Identify the old package's `app_data` directory, its resolved absolute model paths and any explicit `ConnectionStrings__DefaultConnection` value.
2. Finish active work, stop the Oscova Host and Avatar MCP processes and confirm they have exited.
3. Cold-copy the complete `app_data` directory to an empty `.oscova/app_data` directory under your user profile. Do not merge it with another environment.
4. Keep the source copy. Persist `OSCOVA_APP_DATA_DIR` as the copied absolute path for the account or process that launches Oscova.
5. Start the old Oscova version against the copy and confirm the resolved environment path is outside the installed package.
6. Verify sign-in and Vault unlock, then agents, settings, plugins, credentials, a representative user file and model paths. Export an encrypted environment backup.
7. Only then update or uninstall the old tool. If the copy sits at the normal `.oscova/app_data` path, you can remove the override.

An external database selected through `ConnectionStrings__DefaultConnection` is outside the app-data copy. With Oscova stopped, keep a database already in permanent storage, or cold-copy a package-local database with its SQLite sidecar files to an empty permanent destination, point the connection setting at the new absolute path and keep the source. Confirm both the database and environment paths are outside package directories before replacement. Sign-in alone is not proof, because it could still read the old database. For SQLite URI or non-file sources, preserve the storage with that system's backup procedure.

## Three separate identities

Oscova has three sign-in surfaces that never share state:

- The **browser cookie** signs you in to the web UI and never applies to a terminal.
- The **CLI bearer token** is created by `oscova login`, stored in `~/.oscova/auth.json` and used by every command that talks to the host API.
- The **local runtime control token** is written to `~/.oscova/runtime.json` when a host starts on this machine. `start`, `stop` and `restart` use it, so you control a local host without logging in.

`oscova whoami` shows all three. `oscova doctor` explains what to run when one is missing.

Browser cookies and bearer tokens are valid only for the current Host process. After `oscova restart` or any other Host restart, the old token is rejected. The login flow removes the stale entry and offers browser login again. The approval page blocks until you create or unlock your vault, or sign out.

## Global options

Every command accepts these options.

| Option | Meaning |
| --- | --- |
| `-H`, `--host <URL>` | Oscova host URL for client commands. |
| `--quiet` | Suppress informational output. Errors and requested data still print. |
| `--no-color` | Disable colour and ANSI styling. The `NO_COLOR` environment variable does the same. |
| `--no-interactive` | Never prompt. Commands that need input fail with exit code 4. |
| `--json` | Emit machine-readable JSON on stdout. |
| `-h`, `--help` | Print help for the CLI, a branch or a command. |
| `-v`, `--version` | Print the CLI version. |

The host URL is resolved in this order: `--host`, the `OSCOVA_HOST` environment variable, the `host` value in `~/.oscova/cli-config.yaml`, the host you last logged in to, then the default local host.

Reading the output:

- A longer action shows activity and leaves a result behind. Read its wording: a stop or restart request is not a verified restart, a triggered task has started rather than finished, and a staged restore still needs the Host to restart.
- For scripts, use `--json`. JSON and JSONL keep their machine formats, and reading one configuration value with redirected output returns the raw value. `--quiet` never hides the data a command returns.

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | Success. |
| 1 | Internal failure, on the host or in the CLI. |
| 2 | Not authenticated. Run `oscova login`. |
| 3 | Authenticated but not allowed. |
| 4 | Usage error: a bad argument, a missing required option, or a prompt that could not be shown. |

`oscova doctor` always exits 0, because reporting a broken environment is a successful run.

## The interactive gateway

Running `oscova` with no command opens a chat session in your terminal. It starts the local host and signs you in if either is missing, then keeps one thread going until you leave. A session card shows the target host, user, agent, model and thread.

Type a slash command instead of a message:

| Command | What it does |
| --- | --- |
| `/help` | List the session commands and key bindings. |
| `/status` | Show the target host, the CLI login and host state. |
| `/agents` | Switch the current agent. |
| `/model` | Override the model provider for this session. |
| `/thread` | Switch to an existing thread. |
| `/new` | Start a fresh thread. |
| `/clear` | Clear the transcript. |
| `/quit` | Leave the gateway. |

These work only inside the session. The commands in the reference below run outside it.

| Key | What it does |
| --- | --- |
| Up and Down arrow | Recall earlier prompts from this session. Prompts are never saved to disk. |
| Tab | Complete the highlighted slash command in the suggestion list. |
| Left, Right, Home, End | Move the caret within the line. |
| Enter | Send the message. |
| Ctrl+C | Discard what is typed. On an empty line, leave the session. |
| Ctrl+D | Leave the session from an empty line. |

Typing `/` opens a suggestion list filtered as you type.

When an agent needs a tool approved, the gateway shows the tool name and arguments and asks you to allow or deny. Shell and skill-script cards also show the execution target and full command arguments, and approving a skill script runs the whole script. Requests for input appear the same way.

In an interactive colour terminal, streamed replies arrive as plain text and are redrawn with Markdown styling when complete. In a redirected or non-colour terminal the plain text stays as it is, so piped output is clean.

## Command reference

### Host lifecycle

| Command | What it does |
| --- | --- |
| `start` | Launch the host. Add `--detach` to run in the background, `--port <PORT>` and `--host-bind <HOST>` to choose where it binds. |
| `stop` | Stop the owned local Host and Avatar MCP, waiting for both to exit. The Avatar Engine keeps running. |
| `restart` | Restart a running Host only. |
| `status`, `st` | Show host status. |
| `version` | Show the CLI version and, if reachable, the host version. |
| `doctor` | Run read-only diagnostics on the CLI, its host connection and its credentials. |
| `node --host <url> --name <name>` | Connect this computer for project work after browser approval. Add `--allow-http` to consent to unencrypted HTTP and `--detach` to continue after closing the terminal. |
| `node status` | Show whether this computer's enrolled node is connected, reconnecting or stopped. |
| `node stop` | Stop this computer's node and its managed work while keeping enrollment. Works while the server is offline. |

Startup behavior:

- Foreground `oscova start` stays attached. An interactive colour terminal shows an **Oscova is ready** card with the address and process ID. Redirected or non-interactive output prints `Oscova is running at <URL> (pid <PID>).` When Oscova listens on every network interface, the address shown is this computer's loopback address, which a local browser can open.
- Detached startup relays component progress and the optional Avatar question to your terminal. Preparation has no time limit. After preparation, the Host has 90 seconds to report readiness. Cancellation or failure stops the child it launched.
- When selected plugins need installation, startup shows a row per package and a progress bar counting completed attempts, not bytes. A failed component does not stop startup. See [Plugins](references/user-guide-settings-and-admin-plugins.md) for recovery.
- Redirected output, `--no-color` and `--no-interactive` use plain lines instead of the live display. `--quiet` and `--json` suppress component feedback and the readiness message, and never ask the Avatar question.
- `oscova stop` reports normally when the services are already stopped. Remote targets need an authorized CLI login.

### Avatar Engine

The optional Oscova Avatar Engine requires Windows. Install the published release, then start it:

```bash
oscova avatar install
oscova avatar start
```

Installation:

- The default install downloads from the release store through a temporary link from `oscova.ai` and needs no `oscova login`. It shows byte progress, writes into a private temporary directory, and checks the published byte count and SHA-256 before extracting.
- If the connection drops, Oscova keeps the bytes already written, shows a five-second retry countdown, obtains a fresh link and resumes. Press N during the countdown or Ctrl+C at any time to cancel. Cancelling discards the partial download. Under `--quiet`, `--json`, `--no-interactive` or redirected input, the N choice is not offered, so cancel with Ctrl+C or from the calling process.
- For an offline installation, pass a ZIP you already have with `oscova avatar install --source "C:\Downloads\avatar-engine.zip"`. `--source` accepts a local file only. An empty value, missing file or URL is rejected.
- Connection, timeout and service failures keep retrying while the command runs. Validation, permission, release-change and disk errors stop for intervention.
- An existing installation is compared with the published content first. Current content needs no download. Changed, unknown or incomplete content asks for replacement confirmation. Stop a running Engine first, keep disk space for the old Engine, the ZIP and the replacement, and use `--yes` for unattended replacement. A failed verification or extraction keeps the installed Engine.

Running:

- `oscova avatar start` launches the Engine, binds the bridge to `0.0.0.0:11318` for a trusted LAN and opens a `1280x720` window. Use `--address 127.0.0.1` for local access only and `--port <PORT>` to change the bridge port. It runs one short availability check and reports an upgrade or unavailable check without preventing use.
- Starting the process is separate from pairing it with the Host. When connecting from another computer, enter the renderer's LAN address in Avatar settings. See [Connect the Engine](references/avatar-engine-connecting.md).
- `oscova avatar status` shows the stored release identity without network access. Local ZIPs are identified by content, not a release version.
- `oscova avatar stop` and `oscova avatar uninstall` manage the local Engine. Uninstall stops it and removes it immediately without a confirmation prompt.

### Avatar MCP service

Run an independent localhost MCP service without starting the Engine or web host:

```text
oscova avatar mcp start
oscova avatar mcp status
oscova avatar mcp stop
```

- Connect a local MCP client to `http://127.0.0.1:11319/mcp`. `start --port <PORT>` changes the port.
- `start --avatar-url <URL>` saves the downstream Avatar URL after startup succeeds and keeps other Avatar preferences. A running service needs a stop and start to change these options.
- The service shares Avatar settings and pairing with the same app-data root, and can control a LAN Engine while its own listener stays on localhost. `oscova avatar mcp stop` stops only MCP.
- See [Avatar MCP server](references/avatar-engine-mcp-server.md) for pairing, all 30 tools, confirmation limits and the trust boundary.

### Identity

| Command | What it does |
| --- | --- |
| `login` | Sign in this CLI to an Oscova host. |
| `logout` | Revoke and remove this CLI login. |
| `whoami` | Show CLI login and local control state. |

`oscova login` shows a **Sign in** card with a browser approval link that already contains the approval code. If you are signed out in the browser, sign in and create or unlock your vault, then Oscova shows **Approve CLI access** for that link. Select **Approve** to authorize the terminal. If the code has expired, run `oscova login` again.

### Chat

| Command | What it does |
| --- | --- |
| `ask [PROMPT]` | Ask an agent. Omit the prompt to read it from standard input. |
| `threads list` | List chat threads, newest first. |
| `threads show <THREAD_ID>` | Show one thread's details. |
| `threads messages <THREAD_ID>` | Print the messages in one thread. |
| `threads new` | Start a new thread and print its ID. Add `--agent <AGENT_ID>` to pick the agent. |
| `threads rename <THREAD_ID> <TITLE>` | Give a thread a new title. |
| `threads delete <THREAD_ID>` | Delete one thread. |
| `threads clear` | Delete every thread. |

`ask` accepts `--agent`, `--thread`, `--continue` to resume the most recently updated thread, `--model`, `--stream` or `--no-stream`, `--timeout <SECONDS>`, and `--jsonl` for machine events as JSON lines.

### Agents, models, plugins and skills

| Command | What it does |
| --- | --- |
| `agents list` | List every agent with its model and source. |
| `models list` | List the models available to agents. |
| `models test <PROVIDER_ID>` | Check a saved provider. LLamaSharp performs an isolated load and one-token inference; other providers retain connectivity checks. |
| `models local` | List the local model files installed on the host. |
| `models available <PROVIDER_ID>` | List the models one provider offers. |
| `plugins list` | List installed plugins and their status. |
| `skills list` | List installed skills. |
| `skills import <FILE>` | Import a skill package from a local file. |
| `skills remove <SKILL_NAME>` | Uninstall a skill. |

`models test <PROVIDER_ID> --json` emits one result with `outcome`, `reason`, `testKind`, elapsed/load/inference timings, `cleanupConfirmed` and any observed runtime evidence. Exit codes are 0 for passed, 1 for failed, 2 for skipped, 3 for timed out and 130 for cancelled. Local diagnostics allow 60 seconds plus up to five seconds cleanup. A connectivity pass does not prove GPU execution, and an unconfirmed cleanup requires operator attention before another diagnostic.

### Tasks

| Command | What it does |
| --- | --- |
| `tasks list` | List scheduled tasks. |
| `tasks show <TASK_ID>` | Show one task's configuration. |
| `tasks runs <TASK_ID>` | List a task's recent runs. `--limit <N>` defaults to 20. |
| `tasks run-show <TASK_ID> <RUN_ID>` | Show one run's result. |
| `tasks trigger <TASK_ID>` | Run a task now. |
| `tasks delete <TASK_ID>` | Delete a task. |

### MCP servers

| Command | What it does |
| --- | --- |
| `mcp list` | List MCP servers and their connected tools. |
| `mcp add` | Register a server and connect to it. |
| `mcp remove <SERVER_ID>` | Remove a server. |
| `mcp restart <SERVER_ID>` | Reconnect one server. |
| `mcp reconnect` | Reconnect every server. |

- `mcp add` needs `--name`, `--transport http|stdio`, then `--endpoint <URL>` for HTTP or `--command <EXECUTABLE>` with repeatable `--arg <VALUE>` for stdio. For headers, environment variables or a working directory, write the full JSON definition to a file and pass `--file <PATH>`.
- Removing or reconnecting a server can stop work that uses it. Removal asks for confirmation. Restart and reconnect ask when the Host reports active work. Pass `--yes` to confirm in unattended commands.

### Diagnostics and audit

| Command | What it does |
| --- | --- |
| `logs files` | List the log files on the host. |
| `logs read` | Read one page of log entries. Filter with `--file`, `--level`, `--search`, `--page`, `--page-size`. |
| `logs clear --file <NAME>` | Empty one log file. `--file` is required. There is no clear-all. |
| `notifications list` | List notifications, newest first. |
| `notifications read <ENTRY_ID>` | Mark one notification read. |
| `notifications read-all` | Mark every notification read. |
| `notifications clear` | Empty the notification inbox. |
| `tools list` | List governed tools with their network scope and call budget. |
| `egress list` | Read one page of the Egress Audit ledger. Filter with `--page`, `--page-size`, `--from`, `--to`, `--kind`, `--source`, `--search`. |

Tool policies are edited in the web UI, where the network scope and call limit are set together.

### Environment and CLI settings

| Command | What it does |
| --- | --- |
| `environment export` | Write a passphrase-sealed backup. Requires an administrator account. |
| `environment restore --file <PATH>` | Restore a backup onto a fresh install and restart the host. |
| `environment reset` | Restart the selected administrator-managed Host with fresh app data while retaining the previous environment. |
| `environment recover --yes` | With the Host stopped, restore the retained previous environment after an interrupted activation. |
| `environment erase` | Preview and permanently remove the owned local environment, CLI state and Avatar data after confirmation. |
| `update` | Stop the Host, install the newest stable release with its plugin updates, and restart the Host. |
| `update --version <N>` | Install exactly version N, including an older one, for example after a failed update. |
| `update status` | Show the latest application update state and retained recovery paths. |
| `config get [KEY]` | Print one CLI setting, or all of them. |
| `config set <KEY> <VALUE>` | Change one CLI setting. |

The CLI settings are `host`, `default-agent-id` and `default-stream`, stored in `~/.oscova/cli-config.yaml`.

Backups:

- Pass the passphrase with `--passphrase`, or leave it out and the CLI prompts without echoing. Under `--no-interactive` or `--json` it must be passed explicitly.
- This Export Passphrase protects the `.oscb` archive and is unrelated to your Vault Password.
- Restore accepts only the current data format and rejects unversioned or unsupported snapshots before activation.

If startup reports an interrupted restore, run recovery with the exact app-data path from that message:

```powershell
$env:OSCOVA_APP_DATA_DIR = 'C:\Users\you\.oscova\app_data'
oscova environment recover --yes
```

```bash
OSCOVA_APP_DATA_DIR='/home/you/.oscova/app_data' oscova environment recover --yes
```

Recovery needs exclusive access, moves the failed candidate under `_pending` and restores the retained previous environment. Keep the original backup and passphrase until sign-in, Vault unlock, agents, settings, chats and files are verified.

### Reset an environment

Run `oscova environment reset` as an administrator for a clean Operator Setup that keeps the previous environment. Use `--host` to select the Host. Add `--yes` only after finishing important work and stopping Avatar MCP and every external writer.

1. Before confirmation, the command shows the selected Host, the application-data path, what moves, what stays and the preparation warning.
2. After acceptance, the retained-data path stays visible while Oscova waits for the replacement Host.
3. An **Environment ready** card appears only after completion is verified. Acceptance alone is not proof the replacement Host is ready.

What happens:

- The Host moves its database, users, chats, settings, files, credentials, plugins, keys and other reset-owned data to a private dated sibling directory. Models, voices, system logs, CLI configuration, installed tool files, Avatar files and external user folders stay. The sibling is not encrypted and is not an importable `.oscb` backup.
- The replacement Host creates a fresh database, downloads the default plugins again, invalidates all earlier browser cookies and CLI bearer tokens, and opens Operator Setup at the same address. The invoking CLI removes only that Host's old bearer entry. Fresh network settings apply on later restarts unless the original start command supplied explicit binding options.
- Reset is refused for an external database, unsafe linked storage, storage that overlaps CLI, Avatar, update or application files, and any pending environment operation.

If the command reports that reset was accepted but completion is unverified, do not retry. Reopen the same address. If startup reports an interruption, stop Oscova and run `oscova environment recover --yes` with the exact `OSCOVA_APP_DATA_DIR` from the error.

### Erase the local environment completely

Use `oscova environment erase` only to remove all owned local state. It previews the target directories, asks a default-No confirmation, then requires the literal `ERASE`. For automation, supply both confirmations:

```text
oscova environment erase --yes --confirm ERASE --no-interactive
```

- Erase stops and verifies the local Host, Avatar MCP and managed Avatar Engine first. It removes `.oscova`, the selected external Oscova environment with its recognized reset copies, and Windows Avatar Engine saved state: accounts, credentials, keys, agents, files, models inside those directories, update recovery files and the installed Avatar. The next `oscova start` creates a new environment.
- The .NET tool stays installed. Unrelated workspaces, backups exported elsewhere, shared caches, Unreal project files and unselected custom environments are not removed.
- Linked paths, protected locations, unowned external directories and external database overrides are refused. An active startup or update must finish or be stopped first.
- If erasure is incomplete, the command reports the remaining paths and does not start a Host. Resolve the cause, review the preview again and confirm a retry.
- Unlike reset, erase keeps no recovery copy. It is available only from the CLI.

## Update the installed application

Oscova checks for harness and Avatar Engine releases at startup and every six hours. The operator receives a saved notification for new releases, including Avatar releases when no local Engine is installed, and existing mute settings still control live alerts. Release history is on GitHub Releases. NuGet remains the package source, so an unavailable GitHub page does not prevent updates.

### Update from the web UI

1. In the operator navigation rail, select **Check for updates**, **Update available** or **Update check unavailable**. The dialog shows a checking state until the check ends. **Refresh** checks both sources again, and a failed lookup for one does not block the other.
2. Review the release. **Read release notes** opens the available release, and **All release notes** or the **Release notes** link in Help shows the complete history. The plugin table lists every installed plugin with a newer compatible stable release, whether or not Oscova itself has an update. All listed plugin updates install together, previews are never offered, and a plugins-only update keeps the installed Oscova version.
3. Finish important work, confirm you control startup, then select **Update and restart**.

The dialog opens a console on the Host computer running `oscova update --version <VERSION>`, the same command you can run yourself. It stops the Host and Avatar MCP, preserves the Avatar Engine, updates the global tool and starts the new version detached. On first start the new version saves and verifies an encrypted recovery checkpoint, then updates the environment and plugins. The console shows each step, reports the healthy Host and closes.

- Closing the console or pressing Ctrl+C never stops the Host or interrupts the package replacement. If the update was interrupted that way, `oscova start` completes it.
- Without a desktop terminal, for example over SSH, the update runs in the background and `oscova update status` shows progress. Restart MCP with `oscova avatar mcp start` afterwards if needed.
- Downloads can take several minutes. The browser shows a longer-wait notice after five minutes and keeps checking.
- If the Host is still running two minutes after you confirm, the update console did not start. The page reloads and reports that Oscova was not changed. Check the update window on the Host computer, then try again.
- A disconnected browser or expired login does not mean failure: sign in again or run `oscova update status`. An unresolved operation prevents a second update.
- After a completed update, Oscova opens the result in the operator's unlocked session, or on the next visit after sign-in and Vault unlock. It lists only what changed. **See what's new** opens the installed release, **Open Plugins** opens preserved plugins that need attention, and **Close** dismisses it. New optional plugins are not added by an upgrade.

Select **Avatar Engine** in the dialog to review the local installation against the server's publication. On a Windows harness started through `oscova`, confirm **Install Avatar** when absent or **Update Avatar** when its content differs. Every online installation uses the current published archive, independent of the harness version, and current content needs no download. A running managed Engine is stopped and restarted at its previous address and port. A stopped or new Engine stays stopped. The result reports verification or launch failures with recovery information. These actions affect only the harness computer. A standalone web Host or a non-Windows harness shows guidance instead. See [Connecting the Avatar Engine](references/avatar-engine-connecting.md).

### Update from a terminal

The automatic path supports a standard global installation with the default file-backed database. Other installations show terminal guidance. Finish active work, then run `oscova update`, or replace the package yourself and start it. The first start runs the same checkpoint and plugin updates:

```bash
dotnet tool update Oscova.Agents.App --global --version <VERSION> --source https://api.nuget.org/v3/index.json
```

The next successful start records the version and reports the change once. It then checks for newer compatible releases of installed plugins, including third-party packages. Interactive startup asks before each plugin update, with **No** as the default. Unattended startup only reports them. Incompatible plugins are disabled before loading, with their files, settings and data kept. Oscova never asks to reset or erase data during an update, and uninstalling Oscova does not remove the shared .NET SDK.

### Recover from a failed update

- If the update fails before it changes your environment, Oscova stays on the current version and the update dialog shows the reason and **Retry** after sign-in.
- If it fails after the environment changed, the console stays open with the failed phase, the previous version, and the checkpoint, recovery-key and log paths, and offers to go back. From any terminal on the Host computer:

```bash
oscova update --version <PREVIOUS_VERSION>
```

Confirm the restore question. The previous version restores the checkpoint at the original data path, reinstalls its recorded plugin versions and starts. Recovery downloads that version from NuGet, so it needs network access. Nothing is restored without confirmation, and installing an older version after a completed update keeps the current data. `oscova update status` shows the same paths. The operation's `recovery.txt` lists the commands for its data root, including the `dotnet tool update --allow-downgrade` fallback for when `oscova` itself does not run.

## Destructive commands

These commands ask for confirmation before deleting or replacing data: `threads delete`, `threads clear`, `tasks delete`, `skills remove`, `mcp remove`, `logs clear`, `notifications clear`, `environment restore`, `environment reset` and `environment recover`.

- Pass `--yes` to skip the prompt in a script. Under `--no-interactive` without `--yes`, the command exits 4.
- `oscova avatar uninstall` stops and removes the local Engine without a confirmation prompt.

## Worked examples

Start a host in the background, then sign in:

```bash
oscova start --detach
oscova login
```

Ask a question and keep the conversation going:

```bash
oscova ask "what is on my calendar today"
oscova ask "and tomorrow" --continue
```

Run a scheduled task now and read its result:

```bash
oscova tasks list
oscova tasks trigger 4f1c9a2e-....
oscova tasks runs 4f1c9a2e-.... --limit 5
```

Pipe machine output into another tool:

```bash
oscova threads list --json | jq -r '.[].id'
oscova logs read --level Error --json | jq '.entries | length'
```

Check the environment when something is not working:

```bash
oscova doctor
```

## Related articles

- [First-time setup](references/user-guide-getting-started-first-time-setup.md)
- [Agent tasks](references/user-guide-automation-agent-tasks.md)
- [Device nodes](references/user-guide-getting-started-device-nodes.md)
- [Network](references/user-guide-settings-and-admin-network.md)
