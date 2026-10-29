# Get to know Oscova

Oscova Agents is a private AI assistant that runs on a computer you own. Your chats, memory and settings are encrypted there under a Vault Password only you hold, and anything that leaves the machine shows up in the Egress Audit.

After [first-time setup](references/user-guide-getting-started-first-time-setup.md), select **Get to know Oscova** on the chat start screen, or ask your agent "What can you do?" at any time. The agent introduces Oscova and asks where you want to start.

## What you get

- **Private by design**: chats, memory, instructions, settings and task runs are encrypted on disk. No telemetry and no crash reports.
- **Nothing leaves unseen**: the Egress Audit lists every call that leaves the machine.
- **Incognito chats**: they never touch disk.
- **Work while you sleep**: agent tasks run on schedules, webhooks and chains.
- **One box for everyone**: each person in a household or office gets their own sign-in, agents, chats and tasks.
- **Your choice of model**: local models through Ollama, LM Studio or a GGUF file, or a cloud provider you opt into.

## Choose where to start

The agent offers four starting points. You can also type your own answer, and the agent replies to it directly.

### Help me with something right now

- Tell the agent what you need: writing, research, planning or a question on your mind.
- The agent starts on it right away.

### Automate something

- Tell the agent what should happen and when, for example "Every Friday at 5 PM, remind me to send my timesheet."
- The agent creates the task right away, and Oscova shows an approval card. Approve it to save the task.
- The task appears under **Tasks**. New tasks notify you after each successful run, and **Run now** tries one immediately.

See [Agent tasks](references/user-guide-automation-agent-tasks.md) for schedules, webhooks and chains.

### Set up a household or team

- The administrator opens **Administration** and selects **Create user** in the **User Directory** header.
- Each new person gets **User** access. The administrator can turn their **Projects**, **Workspace shell** and **Project shell** permissions on or off.
- Each person creates their own Vault Password at first sign-in. The administrator never sees it.
- Only the administrator can add people. Other users can ask the administrator.

See [Users, roles and PIN](references/user-guide-settings-and-admin-users-roles-pin.md) for sign-in modes and PINs.

### Work with confidential files

- Attach files with the paperclip before sending: PDF and text files, up to 50 per message, 10 MB each.
- Select **Add project** in chat to work inside a folder on this computer or a connected device.
- When the agent uses a cloud provider, the files you share are sent to that provider. Turn on **Local only** in **Main Agent** settings to keep the agent on local models and local tools.
