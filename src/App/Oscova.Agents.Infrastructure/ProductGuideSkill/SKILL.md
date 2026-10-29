---
name: oscova-agents-guide
description: Use when the user asks what you or Oscova can do, is new to Oscova, or wants to get to know it. Also answer questions about using, configuring, administering, automating, troubleshooting, or extending Oscova Agents, including setting up model providers, API keys and credentials, plugins, skills, memory and chat search, tasks and nodes in this app. Do not use for unrelated general computing questions.
---

# Oscova Agents Guide

Use this guide for the installed Oscova Agents release.

- Select the smallest relevant resource from the topic map.
- Read related resources only when the first article points to them.
- Prefer the packaged guide over guesses about product behavior or UI names.
- Say plainly when the packaged guide does not establish the answer.
- Answer with the exact screen path and steps the guide gives. Never point the user to the guide or put a `references/` link in a reply.

## Introduce Oscova

Use this when the user is new, wants to get to know Oscova, or asks what you or Oscova can do.

1. Read `references/user-guide-getting-started-get-to-know-oscova.md`.
2. Open with one or two warm sentences: Oscova runs on their own computer, their data is encrypted there, and they can see everything that leaves it.
3. Follow with these pillars as a list, one line each:
   - **Private by design**: chats, memory, settings and task runs are encrypted on this computer.
   - **Nothing leaves unseen**: the Egress Audit on the [Dashboard](/dashboard) lists every call that leaves it.
   - **Incognito chats**: they are never saved.
   - **Work while you sleep**: tasks run on schedules, webhooks and chains.
   - **One box for everyone**: each person gets their own sign-in, agents, chats and tasks.
   - **Your choice of model**: local models, or a cloud provider you opt into.
4. After that text, in the same reply, call the question tool with one question: header "Where to start?", text "Where would you like to start?", and exactly these options and descriptions:
   - "Help me with something right now": "Writing, research, planning or a question on my mind"
   - "Automate something": "Reminders, reports or checks that run on their own"
   - "Set up a household or team": "A separate sign-in and private chats for each person"
   - "Work with confidential files": "Contracts, statements or records that stay private"
   End the text with the last pillar, because the card asks the question. A card without the text is incomplete.
5. When the answer arrives, give that starting point's steps from the article in a few short lines. Open with the first step itself, for example "Tell me what should happen and when." Link each screen you name, then help the user reach a real result there.
   For "Help me with something right now", skip the tour: ask in one short sentence what they need, then do it.
6. If the user types their own answer, help with it directly without repeating the introduction.

- Keep it short, friendly and plain. No emoji, symbols, dashes as punctuation or headings.
- Speak as yourself: say "ask me", never "your agent".
- Never praise the user's choice or request.
- Some tools load on demand. Before creating a task or reading an attached PDF, call `search_tools` with "create task" or "pdf" unless that tool is already in your tool list. Never use shell commands or the `oscova` command instead.
- A reminder or anything that runs later needs a task. Never write a file or note instead, and never say it is set before the approval card appears.
- Link only these screens: [Dashboard](/dashboard), [Tasks](/tasks), [Administration](/admin), [Main Agent](/settings/agents-ai/main-agent).
- The user finished setup already, so never walk them through language, pictures, model, voice or agent name again.
- Do not say you run on a local model unless the user said so.
- You cannot tell whether the user is the administrator, so phrase administrator steps as "If you are the administrator".

## Topic Map

### User Guide

#### Overview

- [User guide](references/user-guide-overview.md): Set up and use Oscova Agents. Run local or cloud models on your own hardware with encrypted memory, approved tools, scheduled agent tasks and audit evidence.

#### Automation

- [Agent tasks](references/user-guide-automation-agent-tasks.md): Schedule recurring AI agent tasks that run saved instructions on an interval, at a set time, after another task or from a webhook, with encrypted run history.
- [Monitoring runs](references/user-guide-automation-monitoring-runs.md): Review AI agent task run history. Open the task directory, inspect a run's transcript and status, and respond to failed or skipped runs in Oscova Agents.
- [Scheduling](references/user-guide-automation-scheduling.md): Schedule AI agent tasks with interval, one-shot, cron, task-chain or webhook triggers, then pause them or run them manually.

#### Chat And Agents

- [Chat memory](references/user-guide-chat-and-agents-chat-memory.md): How AI agent memory works in Oscova Agents. Encrypted conversation recall, daily and long-term memory notes and private agent lessons, all protected by your Vault Password, and why searching old chats needs an embedding provider.
- [Custom agents](references/user-guide-chat-and-agents-custom-agents.md): Create custom AI agents with their own instructions, model, tools, skills and file access, or install a prebuilt starter agent.
- [Main agent chat](references/user-guide-chat-and-agents-main-agent-chat.md): Chat with a private AI agent in Oscova Agents, with chats encrypted on disk. Send messages and files, approve tools, work in a project folder and inspect what the agent did.
- [Main agent settings](references/user-guide-chat-and-agents-main-agent-settings.md): Configure the main AI agent. Set its name, system instruction, model provider, memory, compaction, tools, skills and workspace folders.
- [Shared agents](references/user-guide-chat-and-agents-shared-agents.md): Share one configured AI agent with other users. Admins and moderators restrict the audience while each user keeps private personal context.

#### Concepts

- [Achievements](references/user-guide-concepts-achievements.md): Achievements, XP and levels in Oscova Agents. See which features you have discovered, how badges derive from your last 30 days of activity and how to turn them off.
- [Agents, tools and skills](references/user-guide-concepts-agents-tools-skills.md): How AI agents, tools and skills fit together in Oscova Agents, and which controls decide what an agent can call.
- [Included tools](references/user-guide-concepts-included-tools.md): Reference of the agent tools included with Oscova Agents, covering what each tool can touch, its default approval and what must be enabled first.
- [Model providers](references/user-guide-concepts-model-providers.md): Connect AI model providers, from local models on your own hardware to hosted services. Set a default and fallback, add an embedding provider and cap spending.
- [Tool governance](references/user-guide-concepts-tool-governance.md): Control which tools an AI agent can call. Set tool selection, discovery, approvals, network scope and daily call limits in Oscova Agents.
- [Workspaces](references/user-guide-concepts-workspaces.md): How AI agent workspaces work. Managed workspaces, private folders and additional folders, and the path containment that protects them.

#### Connections

- [Connected accounts](references/user-guide-connections-connected-accounts.md): Connect Google and Microsoft accounts through OAuth so AI agent tools can read mail, calendars and to-do lists with your permission.
- [Discord](references/user-guide-connections-discord.md): Chat with your AI agent from Discord. Connect a bot, authorize one user ID, pick the agent that replies and approve tools from the channel.
- [MCP servers](references/user-guide-connections-mcp-servers.md): Connect an MCP server to Oscova Agents so agents can call tools from other programs and services, on the server or on your own device.
- [Slack](references/user-guide-connections-slack.md): Chat with your AI agent from Slack. Connect an app through Socket Mode, authorize one member in one workspace and pick the agent that replies.
- [Telegram](references/user-guide-connections-telegram.md): Chat with your AI agent from Telegram. Connect a bot, authorize one numeric user ID, pick the agent that replies and approve tools from the private chat.

#### Getting Started

- [Command line](references/user-guide-getting-started-command-line.md): Install, start, update and control Oscova Agents from a terminal with the oscova command-line tool.
- [Run projects on another computer](references/user-guide-getting-started-device-nodes.md): Connect a device node so AI agents can run project file and shell tools on another computer from any signed-in browser.
- [First-time setup](references/user-guide-getting-started-first-time-setup.md): Set up Oscova Agents for the first time. Create the operator, choose a local or hosted model, set a Vault Password and prepare the main agent.
- [Get to know Oscova](references/user-guide-getting-started-get-to-know-oscova.md): What Oscova Agents does for you, and the four places to start after setup. Help right now, tasks that run on their own, one box for a household or team, and confidential files.
- [Local or cloud models](references/user-guide-getting-started-local-or-cloud-models.md): Run a private, local AI model on your own hardware or choose a hosted provider. Compare Ollama, Local GGUF, OpenAI, DeepSeek and more in Oscova Agents.
- [Voice](references/user-guide-getting-started-voice.md): Talk to your AI agent by voice. Set up local speech models on your own hardware, or OpenAI and ElevenLabs speech, for dictation and spoken replies.

#### Knowledge And Files

- [Backup, restore and reset](references/user-guide-knowledge-and-files-backup-and-restore.md): Export an encrypted Oscova Agents backup, restore it on a fresh install, or reset the environment while keeping the old data.
- [Files](references/user-guide-knowledge-and-files-files.md): Browse, upload, edit and delete files in your agent workspace. Add Library documents for semantic search and grant an agent access to folders.
- [Instructions](references/user-guide-knowledge-and-files-instructions.md): Edit the runtime instruction templates sent to every AI agent run in Oscova Agents, from compaction and memory prompts to goal judging and skills.
- [Library search](references/user-guide-knowledge-and-files-library-search.md): Index reference files so AI agents can find passages by meaning across text, Markdown and PDF files with Library Search.
- [Shared folder](references/user-guide-knowledge-and-files-shared-folder.md): Share files between users on one Oscova Agents server. Use the shared folder on the Files screen, in chat and from agents, including encrypted files every member can open.
- [Skills](references/user-guide-knowledge-and-files-skills.md): Give AI agents reusable skills. Import a SKILL.md package, share it with users, find skills online or let an agent write one in Oscova Agents.

#### Monitoring

- [Egress audit](references/user-guide-monitoring-egress-audit.md): See what data your AI agents sent off your machine. The Egress Audit lists cloud-provider calls, Internet-tool calls, connected-channel replies and cloud embedding activity.
- [Metrics dashboard](references/user-guide-monitoring-metrics-dashboard.md): Read 30 days of AI agent token, estimated cost, tool, task and egress activity in the Oscova Agents metrics dashboard, including the admin per-user view.
- [Session inspector](references/user-guide-monitoring-session-inspector.md): Inspect what an AI agent saw. The session inspector shows saved chat and task-run conversations, tool calls, egress entries and downloadable JSON evidence.

#### Settings And Admin

- [Credentials](references/user-guide-settings-and-admin-credentials.md): Store API keys and bot tokens encrypted under your Vault Password, then reuse them across model, speech, channel and tool settings in Oscova Agents.
- [Execution](references/user-guide-settings-and-admin-execution.md): Choose where AI agents run shell commands and skill scripts. Compare Host, Disabled, Microsoft MXC isolation and the optional Docker environment.
- [General settings](references/user-guide-settings-and-admin-general.md): Set the theme, wallpaper, language, time zone, chat titles, recent-chat resume, follow-up behavior and low-battery protection for your Oscova Agents account.
- [Hooks](references/user-guide-settings-and-admin-hooks.md): Connect Oscova Agents to other systems with outbound command and HTTP hooks, task webhook API keys and outbound HTTP hook secrets.
- [Logs](references/user-guide-settings-and-admin-logs.md): Troubleshoot with the Oscova Agents application logs. Filter by scope, level and search, read metadata and exceptions, export JSON Lines, text or CSV and clear files.
- [Network](references/user-guide-settings-and-admin-network.md): Open Oscova Agents to other devices, add HTTPS with Tailscale Serve, allow private web fetches, set the public app URL and share a QR code.
- [Notifications](references/user-guide-settings-and-admin-notifications.md): Control which events reach the notification bell and inbox, including AI agent task updates, log warnings, system errors and UI sound cues.
- [Operator setup](references/user-guide-settings-and-admin-operator-setup.md): Create the single operator account on a fresh Oscova Agents installation with no users, or restore an encrypted backup instead.
- [Plugins](references/user-guide-settings-and-admin-plugins.md): Install, update, enable and uninstall Oscova Agents plugins that add local speech, model providers, channels and tools.
- [Privacy](references/user-guide-settings-and-admin-privacy.md): Private AI agent settings. Keep agent runs offline with local models on your own hardware, sign out idle browsers automatically and see what an agent run can send out.
- [Profile](references/user-guide-settings-and-admin-profile.md): Update your Oscova Agents account, including name, email and password, the Vault Password that encrypts your data, duress credentials and your profile picture.
- [Users, roles and PIN](references/user-guide-settings-and-admin-users-roles-pin.md): Create users, choose Standard or Home sign-in, set PINs, manage OAuth providers and the GitHub token, and delete accounts in Oscova Agents.

### Developer Guide

#### Overview

- [Developer guide](references/developer-guide-overview.md): Choose a supported extension path for plugins, tools, governance, runtime behavior, memory, skills, or testing.

#### Examples

- [Echo plugin](references/developer-guide-examples-echo-plugin.md): Study the source-backed Echo plugin fixture for Oscova Agents.
- [Testing plugins](references/developer-guide-examples-testing-plugins.md): Test plugin loading, reflected tools, activation, and package installation at the supported boundaries.

#### Governance

- [Tool policies](references/developer-guide-governance-tool-policies.md): Declare and resolve tool approval and network policies.
- [Workspace path safety](references/developer-guide-governance-workspace-path-safety.md): Build file tools that translate, authorize, and report paths without escaping active workspace grants.

#### Plugins

- [Compiled plugin settings](references/developer-guide-plugins-compiled-settings.md): Register plugin-owned settings pages and compiled components in host-defined slots.
- [Plugin context](references/developer-guide-plugins-context.md): Use the narrow host services supplied to Oscova Agents plugin contributions.
- [Plugin credentials](references/developer-guide-plugins-credentials.md): Declare and resolve user-scoped API-key credentials from Oscova Agents plugins.
- [Build your first plugin](references/developer-guide-plugins-first-plugin.md): Create a first trusted plugin for the Oscova Agents agent framework.
- [IAgentPlugin](references/developer-guide-plugins-iagent-plugin.md): Implement the entry point used by Oscova Agents plugins.
- [Plugin manifest](references/developer-guide-plugins-manifest.md): Define the discovery, loading, and package contract for an Oscova Agents plugin.
- [Plugin builder](references/developer-guide-plugins-plugin-builder.md): Register supported toolset, model, speech, channel, and credential contributions from an Oscova Agents plugin.

#### Runtime

- [Agent as tool](references/developer-guide-runtime-agent-as-tool.md): Use one Oscova Agents agent as a callable tool from another agent.
- [Agent runtime](references/developer-guide-runtime-agent-runtime.md): Build, cache, refresh, and run agents through the reusable Oscova.Agents runtime.
- [Context providers](references/developer-guide-runtime-context-providers.md): Add ordered instructions, messages, tools, and post-run processing to Oscova Agents.
- [Conversation persistence](references/developer-guide-runtime-conversation-persistence.md): Persist per-user conversation history and framework session state beneath a caller-selected users root.
- [Runtime hooks](references/developer-guide-runtime-hooks.md): Observe or block agent sessions, prompts, task runs, and tool calls with external hooks.

#### Skills

- [Memory and skills](references/developer-guide-skills-memory-and-skills.md): Understand progressive skill disclosure and memory in Oscova Agents.

#### Toolsets

- [Toolsets](references/developer-guide-toolsets-toolsets.md): Discover, name, register, and invoke built-in, plugin, and MCP tools in Oscova Agents.

### Avatar Engine

#### Overview

- [Bridge Service](references/avatar-engine-bridge-service.md): Understand the HTTP bridge between Oscova Agents and the Avatar Engine.
- [Connect the Engine](references/avatar-engine-connecting.md): Connect Oscova Agents to the separate Avatar Engine runtime.
- [Engine HTTP API](references/avatar-engine-http-api.md): Reference the standardized HTTP API exposed by the Oscova Avatar Engine.
- [Avatar MCP server](references/avatar-engine-mcp-server.md): Connect local assistants to Avatar Engine tools with the existing Oscova CLI.
- [Avatar Engine](references/avatar-engine-overview.md): Connect Oscova Agents to the separate Oscova Avatar Engine runtime.
- [Avatar Settings](references/avatar-engine-settings.md): Configure avatar-related settings in Oscova.
- [Speech and Avatar Flow](references/avatar-engine-speech-flow.md): Understand how speech and avatar state move through Oscova.
- [Troubleshooting](references/avatar-engine-troubleshooting.md): Troubleshoot Avatar Engine connection and runtime issues.
