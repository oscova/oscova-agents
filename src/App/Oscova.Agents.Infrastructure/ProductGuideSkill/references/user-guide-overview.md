# User guide

Oscova Agents is a self-hosted AI assistant and Agent OS: a managed place to chat with the main assistant, create specialized agents, approve tools, schedule repeatable work, inspect memory and monitor usage.



> **Note:**
> **Agent OS** means the managed environment around an AI model. It gives agents identity, tools, skills, memory, workspace files, account rules and logs so they can do useful work without every capability being built into the model itself.

## Start here

On a new installation:

1. Create the installation's operator. Oscova shows Operator Setup only while the database has no users.
2. Sign in and create your Vault Password.
3. Complete onboarding: choose a language and a required profile picture, add a model provider, optionally download speech models and name the main agent. You can change the picture or upload your own later from Your Profile.

If Oscova is already set up, start with Main Agent Chat. That is the daily workspace where you send messages, attach files, switch agents, choose a chat provider, approve tool calls and inspect resulting activity and files. On an empty chat, Oscova checks whether the selected provider is ready. Follow the start-screen action to add a provider, choose a default or retry a failed check before sending.

Choose the next article by the problem you are solving:

- Need the app to answer at all? Start with [Local or cloud models](references/user-guide-getting-started-local-or-cloud-models.md).
- Need safer unattended work? Read [Tool governance](references/user-guide-concepts-tool-governance.md) before [Agent tasks](references/user-guide-automation-agent-tasks.md).
- Need local-only behavior? Read [Privacy](references/user-guide-settings-and-admin-privacy.md), then review model providers and tool policies.
- Need evidence after something ran? Use [Metrics dashboard](references/user-guide-monitoring-metrics-dashboard.md), [Session inspector](references/user-guide-monitoring-session-inspector.md) and [Logs](references/user-guide-settings-and-admin-logs.md) together.

## Main areas

The navigation rail holds **Chat**, **Tasks**, **Dashboard** for your usage and task activity, and **Notifications**. Operators also see **Administration**. **Settings** opens the settings page and its category panel. In desktop chat, **Toggle sidebar** hides or restores the projects and conversation list. On smaller screens, **Show chats** opens that list over the conversation.

The guide follows the same areas:

- **Getting Started** covers first launch, model choice and voice.
- **Core Concepts** explains agents, model providers, tools, skills and governance.
- **Chat and Agents** covers live chat, run evidence, main agent settings, custom agents, shared agents and memory.
- **Automation** covers agent tasks, schedules, webhooks and run history.
- **Monitoring and Audit** covers metrics, egress review and session inspection.
- **Knowledge and Files** covers files, skills, prompt templates and import/export.
- **Connections** covers MCP servers, connected accounts, Telegram, Slack and Discord.
- **Settings and Administration** covers profile, credentials, network, privacy, users, roles and logs.

## How Oscova stores work

Each user has a managed workspace under the app data folder. Files, skills, prompts, memory, chat threads and profile pictures are scoped to that user. Settings are stored separately so backups can move an environment between installations.

> **Important:**
> Deleting the app data folder or a user's workspace folder deletes that user's files, memory, prompts and settings. Export a backup before moving or rebuilding an installation.

Agents do not see every server path automatically. Each run receives a limited file view based on the selected agent, selected task scope, uploaded chat attachments and any external folders you granted. In sandboxed runs, those folders appear to the agent as paths such as `/workspace/<name>`, `/self` or `/mnt/<label>`.

## Connection recovery

- When the connection drops, Oscova makes up to three automatic reconnect attempts, waiting two seconds before each attempt.
- Choose **Retry** if automatic reconnection fails.
- If your session has ended, choose **Reload** to continue. Unsaved changes may be lost.

## Common first checks

- Add and test at least one chat model provider.
- Configure an embedding provider and allow time for scheduled indexing before relying on conversation recall. Explicit daily and long-term memory notes do not require embeddings.
- Review each agent's tools and file access before scheduling unattended tasks.
- Download speech models or configure hosted speech providers before using voice controls.
- Use the metrics dashboard after a few chats or tasks to check token, cost, tool and task activity.

## Related articles

- [First-time setup](references/user-guide-getting-started-first-time-setup.md)
- [Main agent chat](references/user-guide-chat-and-agents-main-agent-chat.md)
- [Metrics dashboard](references/user-guide-monitoring-metrics-dashboard.md)
- [Agents, tools and skills](references/user-guide-concepts-agents-tools-skills.md)
- [Agent tasks](references/user-guide-automation-agent-tasks.md)
