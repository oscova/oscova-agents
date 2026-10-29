# Model Providers

Credential status appears beneath the provider name. Test results appear above the row of Edit, Duplicate, Test, and Delete actions. Narrow screens show labelled groups.

Model Providers connects your account to chat and embedding models. Chat needs a configured default provider.

> **In plain terms:** a provider is the saved connection to a local or cloud AI model.

## Configure providers

- Add, edit, duplicate, test, or delete chat providers from the table.
- DeepSeek is available for hosted chat, including thinking and tool calls. Configure it through the chat provider editor.
- In the chat provider editor, Ollama and download-capable plugins offer **Download model** in the **Model ID** section. Downloading and saving the provider are separate actions.
- The first provider becomes default, while later default and fallback choices save immediately.
- Saved provider changes apply to later runs even if removing outdated agent or task references fails.
- The metrics dashboard uses reported input and output tokens with the rates saved when each run occurs.
- Cached input uses the standard input rate, while other charges are excluded.


## Embeddings and failures

- A shared provider has one combined budget for all consumers. Only its owner can change it or choose **Continue anyway** for one interactive turn.
- Consumers see a budget-reached notice and reset time above the chat input. The owner's spending and limits remain private.
- The operator can allow personal providers in **Edit user**. Their settings, credentials, and budgets belong to that user.
- Limits check completed estimated spending before new work. Active work can finish and exceed a limit. Prices are saved with usage, and local inference records no charge.
- Exhausted embedding budgets pause new indexing and semantic search. Saved files and memory remain intact. Scheduled indexing or a later search retries after eligibility returns.

- Embedding providers power conversation memory and semantic Library search, not chat.
- Ollama, OpenAI, OpenRouter, and OpenAI-compatible endpoints support embeddings. Other provider types are chat-only.
- A fallback protects only the default provider and only before response content appears.
- Fallback uses its own saved chat options and keeps any stricter output limit for the run.
- If **Test** fails, check endpoint, model, credential, and local service availability.
- LLamaSharp **Test** performs a separate load and one-token inference, needing additional memory. It allows 60 seconds plus five seconds cleanup. Busy checks skip, and timeout or cancellation never count as a pass.
- Inference results show observed backend and offloaded layers when available. If cleanup is unconfirmed, consult the server log before retrying. Other providers retain connectivity tests.

> **Note:** restart Oscova after installing or enabling a model provider plugin.

## Related

- [Chat Memory Store](chat-memory.md) - inspect stored memories.
- [Main Agent](settings.main-agent.md) - choose an agent's default provider.
- [Set model budget](dialog.edit-model-budget.md) - help for this dialog.
- [Model budget reached](dialog.model-budget-warning.md) - help for this dialog.
- [Download provider model](dialog.provider-model-download.md) - help for this dialog.
- [Download GGUF model](dialog.local-model-download.md) - help for this dialog.
- [Configure chat provider](dialog.model-provider-editor.md) - help for this dialog.
- [Configure embedding provider](dialog.embedding-provider-editor.md) - help for this dialog.
- [Chat provider details](dialog.model-provider-details.md) - help for this dialog.
