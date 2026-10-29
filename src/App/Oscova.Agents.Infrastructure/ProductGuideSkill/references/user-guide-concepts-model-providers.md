# Model providers

A model provider is a saved connection to an AI model. Oscova Agents uses providers for chat responses, scheduled task runs, conversation memory and semantic Library search, and you can mix local models running on your own hardware with hosted services.

> **Note:**
> **Model** means the AI system that produces text or embeddings. **Provider** means the saved connection details Oscova uses to reach that model.

Who can manage providers:

- The operator's providers are available to regular users, who choose among them without seeing credentials or private endpoint settings.
- The operator can grant **Manage personal model providers** in **Edit user** so a user can create private connections with their own credentials and budgets. Revoking the permission keeps the definitions but prevents their use.
- Chat and embedding selections are personal. Speech uses its own provider settings.

## Chat providers

A chat provider answers messages. Oscova supports local GGUF models, Ollama, OpenAI, DeepSeek, Groq, OpenRouter, NVIDIA NIM, Google Gemini, Anthropic and generic OpenAI-compatible endpoints. LM Studio is available as an OpenAI-compatible preset.

- Local GGUF needs the Oscova LLamaSharp provider plugin installed and enabled. Foundry Local has its own provider plugin on Windows x64, Linux x64 and Apple Silicon macOS, and needs no API key. Restart Oscova after installing or enabling either plugin.
- DeepSeek starts with `deepseek-flash` and thinking enabled. Link a DeepSeek API key and use **Fetch models** to choose a model. Flash accepts images, while `deepseek-v4-pro` is text-only. Clearing **Supports thinking outputs** disables thinking for this provider. DeepSeek is chat-only, so semantic memory and Library search need a separate embedding provider.

Each provider row shows the selected model, endpoint, credential state, input and output prices, capability badges, last test result and whether it is the default or fallback. Row actions edit, duplicate, test or delete the provider. If no default exists, the first provider added becomes the default.

## Add or edit a provider



1. Choose **Provider Type** first so Oscova applies the known defaults.
2. Enter a **Provider Name**, the label shown in selectors and tables.
3. Point the provider at its model: choose a GGUF file for a local provider, set **Base URL** for a hosted or compatible endpoint that needs an override, and select or download a model ID.
4. Link a **Saved API Key** when the provider needs one. **Manage credentials** creates or updates the secret first.
5. Set the capability flags, **Context Window Size** and any **Advanced options**, then save. Use the provider row's **Test** action to check the saved configuration.

Provider tab details:

- Local providers choose a GGUF file from the local models folder and can download a direct HTTP or HTTPS GGUF URL.
- **Fetch models** asks supported providers for available model IDs. Choosing a fetched model can fill the context window and capability flags when the provider returns metadata.
- Ollama and download-capable plugins offer **Download model** in the **Model ID** section. For Ollama, type a registry tag and download it without fetching first, including a model that was removed or never installed. The Ollama server at the configured Base URL stores it.
- Download-capable providers show license details and installation state when their catalog supplies them. Review a Foundry Local model's license before downloading. Installed catalog selections disable download. Foundry Local uses its own model cache instead of the GGUF models folder.
- **Endpoint scope** stays on auto-detect or forces **Treat as local** or **Treat as cloud** for Offline mode.
- **Supports images** makes an image analysis tool available for supported image attachments and local image paths in chat runs on that provider.
- **Supports thinking outputs** passes the model's reasoning-style outputs through supported runtime paths, including Ollama and OpenAI-compatible providers. Thinking works with tool calls and continues through saved history.
- **Context Window Size** also powers web-chat warnings when a draft is estimated to exceed the model's usable input window.
- **Input price** and **Output price** are standard USD rates per million tokens for dashboard estimates. Leave an unknown rate blank and enter zero when the rate is free.
- Ollama providers can set **Keep alive**, sent as `keep_alive` with chat requests. Blank uses the server default. `5m` and `1h` keep the model loaded for that time, `-1` keeps it loaded indefinitely and `0` unloads it after the response.

For Local GGUF models with complete metadata, estimated memory fit appears below **Context Window Size**. It includes model weights and an F16 context cache, excluding runtime buffers and other loaded models. CPU uses 75% of the smaller known physical RAM or process-memory budget. The process budget is an approximation. Full CUDA offload uses 90% of one unambiguously identified NVIDIA adapter's total dedicated memory. Apple Silicon unified memory is counted once. These capacity estimates do not measure free memory or prove that inference will succeed.

The LLamaSharp CUDA edition defaults to **Full GPU offload**. Choose **Custom GPU layers** under **Advanced options** to request fewer layers. Partial placement shows the total footprint but leaves per-device fit unknown. Save commits options only to this provider, Cancel discards the draft, and Duplicate makes an independent copy. The CPU edition preserves saved CUDA options but ignores them. Changing provider type clears those options. Ollama and Foundry Local manage their own placement, so host RAM or VRAM cannot establish their suitability. Unknown hardware or metadata still permits Save.

For LLamaSharp, **Test** loads the saved model and performs one short inference in a separate process. It can pass, fail, skip when another check is busy, time out or be cancelled. The check has a 60-second deadline and up to five seconds for cleanup. It needs additional memory while other models remain loaded. A pass reports observed backend, device and offloaded layers when native evidence is available. Other providers retain connectivity tests, which do not prove acceleration. A previous result does not establish current spare memory. If cleanup is unconfirmed, use the server log's process details before trying again.

On the **Chat Options** tab, tune request defaults such as temperature and sampling limits. Leave provider defaults unless you know the model needs a different style.

### Download a model

Keep the progress dialog open until completion. Ollama percentages describe the current layer, and preparation stages can have no percentage. **Cancel** or the header close button cancels the attempt. After completion, **OK** refreshes the model list and keeps the typed ID. Downloading does not save the provider configuration.

If downloading fails, the editor shows the requested model and error in the **Model ID** section. Check that the tag exists in the registry and the server is reachable, then retry **Download model**.

## Which provider a run uses

When a chat or task runs, Oscova resolves the provider in this order:

1. A provider selected for that single chat run or task.
2. The selected agent's saved provider override.
3. Your default chat provider.

The fallback provider applies when the effective provider is your default, including an explicit selection of that same provider. Oscova tries it when the default cannot initialize or fails before any response content is emitted. Fallback uses its own saved chat options and keeps any stricter output limit for the run. A different provider chosen for one run, task or agent gets no fallback.

Saved provider changes apply to later runs even if removing outdated agent or task references fails afterward.

## Embedding provider

An embedding provider converts text into vectors so Oscova can search memory by meaning. It is configured separately because the best chat model is not always the best memory-search model.

Without one, agents cannot search past chats or Library files by meaning. Daily memory, Dreaming and saved memory notes keep working.

To add one:

1. Open **Settings** > **Agents & AI** > **Model Providers**.
2. In the **Embedding Provider** card, select **Add embedding provider**.
3. Choose a type that supports embeddings, such as Ollama or OpenAI, fill in the fields below, then save. The new provider becomes your selected embedding provider.

> **Note:**
> **Embedding** means a numeric representation of text. Similar text produces similar vectors, which makes memory search possible.

- The editor asks for provider type, name, optional base URL, model ID, vector dimensions, endpoint scope and a saved credential. The vector dimensions must match the embedding model output.
- Ollama, OpenAI, OpenRouter and OpenAI-compatible endpoints support embeddings. Local GGUF, Foundry Local, Google, Anthropic, DeepSeek, Groq and NVIDIA NIM are chat-only, so the selector excludes them.
- Saving a changed embedding identity (provider, type, base URL, model ID or dimensions) resets conversation-memory vectors and queues Library reconciliation. Library source files are kept and re-embedded. Semantic results can be incomplete while rebuilding.

> **Important:**
> Searching indexed conversation memory needs both an embedding provider and **Enable Memory** on the agent. Chat and explicit daily or long-term memory notes work without embeddings. See [Chat memory](references/user-guide-chat-and-agents-chat-memory.md).

## Test a provider and estimate costs

Use **Test** after creating or editing a provider. A pass proves the current health check succeeded, not that the configured model or every future prompt will work.

The metrics dashboard estimates cost from reported input and output tokens and the two rates saved when usage was recorded. Cached input uses the standard input rate, and other charges are excluded. A partial estimate means some recorded usage cannot be fully priced. If nothing can be priced, the amount is unavailable. Keep rates current if you rely on cost summaries.

## Spending limits

Choose **Set budget** or **Edit budget** beside a provider you own to set a daily limit, a calendar-month limit or both in USD, then select **Save**. Leave a limit blank to disable that period. Periods reset in the provider owner's configured time zone, and chat and embedding configurations use the same controls.

- An operator-owned provider has one combined allowance across everyone using it. A USD 20 limit allows USD 20 of combined estimated spending. A user's personal providers have separate budgets.
- When a limit is reached, new chat turns and task attempts stop before model work begins. The provider owner can choose **Continue anyway** for one interactive operation. Messaging channels ask through their confirmation flow. Another user on the operator's provider must contact the operator or pick an alternative. Background tasks skip the attempt and keep their schedules. Already started operations finish, so concurrent work can exceed a cap.
- An exhausted embedding budget pauses new indexing and semantic searches with an explanation. Stored content, vectors and committed indexing progress stay intact, and scheduled indexing and later searches recheck availability. Saving a changed limit also queues Library reconciliation.
- Your row and the metrics Dashboard show your estimated spending and limits. A short status above the composer shows the reset time when a limit is reached. Other consumers see only the budget-reached notice, reset time and recovery guidance. Optional 80% notifications are sent once per provider and period.
- Recorded prices preserve historical costs after later edits. Local inference records zero cost. Incognito usage counts anonymously without conversation, agent or task identity. Missing token reports, missing prices or unavailable accounting show as incomplete estimates. Speech and separately billed tool services are outside these budgets, and the limits are not guaranteed invoice ceilings.

## Offline mode and endpoint scope

Offline mode blocks public internet providers and egress tools during agent runs. Oscova classifies each provider by its effective URL:

- Local GGUF and Foundry Local providers are local.
- Loopback, private LAN, link-local and unique-local IPv6 addresses are local.
- Public hosted endpoints are cloud.
- **Endpoint scope** overrides auto-detection when a custom proxy or gateway would be classified incorrectly. Forcing **Treat as local** also removes protection for a public URL, so use it only for a trusted endpoint.

Agent-level **Local only** follows the same blocking rules even when Offline mode is off, including for embedding search and indexing.

## Change providers safely

- Add and test a replacement provider before deleting an old one.
- Keep at least one default chat provider selected.
- Link saved credentials for hosted providers that require API keys.
- Deleting a provider clears default or fallback selection when necessary and removes matching agent and task overrides.
- Revisit embedding settings before changing dimensions after memories already exist.
- Use **Endpoint scope** sparingly. It changes how local-only enforcement treats the provider.

## Related articles

- [Local or cloud models](references/user-guide-getting-started-local-or-cloud-models.md)
- [Credentials](references/user-guide-settings-and-admin-credentials.md)
- [Agent tasks](references/user-guide-automation-agent-tasks.md)
