# Download provider model

Follow the requested model's download progress on its configured provider.

- Keep the dialog open while the download runs.
- **Cancel** or the header close button cancels this attempt. Keep the dialog open to finish.
- Ollama shows the current layer's percentage and preparation status. A layer reaching 100% does not mean the model is ready.
- **OK** becomes available after the provider confirms completion. Closing it refreshes the caller's model list.
- A failure closes this dialog and shows the model and error in the calling screen. Correct the model ID or endpoint there and select **Download model** to retry.
- Ollama stores the model on the configured Ollama server. Foundry Local uses its own model cache.

> **Note:** Successful download makes the model available to the editor. Save the provider separately.

## Related

- [Model Providers](settings.model-providers.md) - return to the parent screen's help.
