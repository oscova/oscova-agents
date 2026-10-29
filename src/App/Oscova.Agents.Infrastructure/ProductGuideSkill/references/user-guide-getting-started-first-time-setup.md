# First-time setup

First-time setup creates the installation's one operator, connects Oscova Agents to a local or hosted model and prepares the main agent you will use in chat.

If Oscova is not installed yet, follow the [command-line installation guide](references/user-guide-getting-started-command-line.md), then run `oscova start` and open the address it prints.

> **Note:**
> **Main agent** means the default assistant Oscova creates for everyday chat. You can later edit its name, standing instruction, model provider, tools, skills, memory and file access.

## Before you start

- Confirm the app can write to its local app data folder.
- Decide whether you want a local model, Ollama or a hosted provider such as OpenAI.
- Keep the operator username and password somewhere safe.
- Choose a separate Vault Password and store it somewhere safe. Oscova cannot recover it.
- Create API keys or environment variables before configuring hosted providers.
- Plan time for speech model downloads if you want local voice input or spoken replies.
- On macOS, use macOS 15 or newer on Intel x64 or Apple Silicon. Install only the optional capabilities you need.

## Choose optional plugins at startup

Interactive startup offers the optional plugins in one checklist, all unchecked, including OpenAI for hosted speech. You can install an unchecked plugin later through **Settings > Plugins** and restart. OpenAI model setup does not require the speech plugin.

Selecting local recognition or synthesis on Windows x64 or Linux x64 asks for its CPU or NVIDIA CUDA edition. CPU is the initial choice, and macOS uses CPU without another question. NVIDIA runtime prerequisites are installed separately.

## Create the operator

When Oscova starts with no users, it shows Operator Setup instead of the sign-in screen. Enter the requested account details and create the account. Oscova then opens the sign-in screen with a message that the operator was created.

- Operator Setup does not appear again while any user exists. If every user is removed from the database, it appears on the next launch.
- The operator is recorded as the host owner for local host-control features. Oscova also writes a local CLI access token so the packaged `oscova` tool can talk to the same host without a separate login.

## Restore an existing backup

Operator Setup can restore a `.oscb` backup before any users exist.

1. Choose a backup up to 500 MB.
2. Enter the passphrase used during export.
3. Keep the browser open while Oscova validates, stages and restarts the host, then checks for the replacement host.

Restore replaces restorable app data such as users, agents, chats, settings, credentials, plugins and each user's wrapped vault key. Per-user logs are included. Machine-local authentication keys, models, voices, shared system logs and pending restore files are excluded.

> **Important:**
> Restore is blocked once any user exists. Use a fresh installation or a fresh app-data folder when moving from a backup.

## Complete onboarding

After first sign-in, Oscova requires you to create your Vault Password. It protects saved credentials and protected data, stays separate from the Account Password and must be entered again after a restart or restore.

Onboarding then guides your personal workspace through five steps. It cannot be closed without completing it.

1. **Language** saves immediately and reloads the app in the selected language.
2. **Profile** collects your preferred name and profile picture. The name starts as the first word of your account's full name and your account's picture is already selected. Select **Continue** to save them.
3. **Model** configures any available first-party local or hosted provider.
4. **Speech** downloads models for installed local speech plugins. Each installed plugin starts checked and its checkbox reveals its download card. A missing plugin is named with where to install it: the administrator installs it in **Settings > Plugins**. Configure hosted OpenAI speech later in **Settings > Speech**.
5. **Identity** starts with **Olivia**, **Friendly** and the main agent's picture selected. Select **Finish** to save the main agent name, picture and persona.

Your preferred name and profile picture, and the main agent's name and picture, are required. You can change them later in **Your Profile** and **Main Agent** settings. **Skip step** leaves an optional step unsaved. **Skip all** takes you to Profile if its required choices are unsaved, then to Identity. Earlier saved choices are preserved. If you reload the page or Oscova restarts, setup resumes after the last step you finished.

### Model step

The model step lists the available providers: Ollama, LM Studio, Local GGUF, Foundry Local, OpenAI, DeepSeek, Anthropic, Google Gemini, Groq, NVIDIA NIM and OpenRouter, followed by providers added by installed plugins. Choices appear only when their provider runtime is available. Ollama starts selected. Custom OpenAI-compatible connections are set up later in Model Providers settings.

- Ollama and LM Studio require a valid HTTP or HTTPS base URL.
- Hosted providers accept a pasted API key. The operator can also use an environment-variable name whose value is available to the Host process. Restart the Host if it cannot see a variable added outside the process. Regular users must store a personal value when they have permission to configure providers.
- Select **Fetch models**, then choose a returned model. The recommended or saved model stays selected when the provider returns it. Discovery keeps a pasted key or resolved environment value in memory. **Continue** saves either the key or only the environment-variable name. Changing the provider, credential source, active credential input or URL clears the fetched choice.
- Local GGUF uses the default model configured for the app and can start its download. You can continue while the download runs.
- Foundry Local shows its model catalog, license details and installation state. Download the selected model before continuing.

**Skip step** continues without a model provider. Chat stays paused until you set a default provider in Settings.

> **Tip:**
> You can skip voice during onboarding. The microphone, speaker and hands-free buttons stay disabled until speech is ready later.

Scheduled tasks wait until onboarding completion is saved through **Finish**. Due work can start on the next scheduler check, within about 30 seconds while Oscova is running. Skipping an optional step does not release scheduled work.

## What to check next

- Open chat and select **Get to know Oscova** for a short tour. See [Get to know Oscova](references/user-guide-getting-started-get-to-know-oscova.md).
- If chat is paused, add or test a default model provider.
- Review the main agent's instructions, tools, memory and file access.
- Add an embedding provider before depending on memory.
- Configure credentials before enabling providers or integrations that need secrets.

## Related articles

- [Local or cloud models](references/user-guide-getting-started-local-or-cloud-models.md)
- [Voice](references/user-guide-getting-started-voice.md)
- [Main agent settings](references/user-guide-chat-and-agents-main-agent-settings.md)
- [Model providers](references/user-guide-concepts-model-providers.md)
