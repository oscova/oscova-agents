# Configure chat provider

Connect a chat model and choose the defaults used for its later runs.

## Connect a model

- Choose **Provider Type**, then enter **Provider Name** and **Model ID**.
- Use a valid **Base URL** when overriding an endpoint.
- **Fetch models** supplies model IDs where supported.
- For Ollama, type the model tag and select **Download model** in the **Model ID** section, even if the model is not installed. The server must be running and the tag must exist in its registry.
- Download-capable plugins also offer **Download model**. Foundry Local catalog selections show their license and installation state, and installed selections disable download.
- For Local, select a GGUF file or download one using a direct HTTP or HTTPS URL.
- Local GGUF requires the enabled LLamaSharp provider plugin.
- Foundry Local requires its enabled plugin and a downloaded model.
- Select a **Saved API Key** for authenticated providers.
- DeepSeek fills its hosted endpoint and `deepseek-flash` model. Link a DeepSeek API key, then use **Fetch models** to choose another model and apply its reported capabilities.

## Choose defaults

- Set image and thinking capabilities to match the model.
- For DeepSeek, **Supports thinking outputs** enables thinking with tool calls. Clear it to request answers without thinking. Flash supports images, while Pro is text-only.
- **Context Window Size** accepts zero for unknown capacity.
- Memory fit uses the requested placement and available metadata. CPU excludes GPU memory, full CUDA needs one identifiable GPU, and manual layers leave per-device fit unknown. Ollama and Foundry manage placement themselves.
- LLamaSharp CUDA **Advanced options** choose full offload or 1–2147483647 GPU layers. Save commits options to this provider only. Cancel discards them, and changing provider type clears them.
- **Treat as local** requires an endpoint you trust.
- Prices accept non-negative USD per million tokens, or blank for unknown rates.
- Ollama **Keep alive** accepts durations, `-1` for indefinite loading, or `0` to unload after responding.
- **Chat Options** controls sampling, penalties, seed, output length, and tool-call behavior.
- **Clear** leaves an option to the provider.
- **Reset to defaults** changes the editor draft.
- **Save** applies the provider, while **Cancel** discards edits.
- Downloading does not save this form. A download failure appears in the **Model ID** section. Correct the tag or endpoint and retry.

> **Note:** Unknown hardware still permits Save. Estimates exclude runtime buffers and other models. Test the saved provider separately to check inference.

The footprint includes weights and an F16 context cache. CPU uses 75% of the smaller known physical RAM or approximate process budget. Full CUDA uses 90% of one identifiable GPU's total memory. Unified memory is counted once. These are capacity budgets, not current free memory. The separate Test needs extra memory alongside loaded models.

## Related

- [Model Providers](settings.model-providers.md) - return to the parent screen's help.
- [Download provider model](dialog.provider-model-download.md) - progress and cancellation help.
