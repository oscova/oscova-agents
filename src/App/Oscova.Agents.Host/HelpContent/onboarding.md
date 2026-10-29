# Onboarding

Onboarding appears after first sign-in to get your personal workspace ready. It walks through language, preferred name and profile picture, model setup, optional speech downloads, and the main agent identity.

> **In plain terms:** complete your profile and the main agent's identity to enter your workspace. Model and speech setup can wait.

> **Note:** you create and unlock your Vault Password before this wizard loads, so hosted API keys can be saved normally.

## Complete the steps

- Language saves immediately and reloads the app.
- Confirm your preferred name, prefilled from your account, and your profile picture, already selected. Select **Continue** to save both after your vault is unlocked.
- Ollama starts selected. Scroll the model choices to see every available provider.
- Local choices include Ollama, LM Studio, Local GGUF, and Foundry Local.
- Hosted choices include OpenAI, Anthropic, DeepSeek, Google Gemini, Groq, NVIDIA NIM, and OpenRouter.
- Hosted providers accept a pasted key or an environment-variable name.
- Environment names are suggested from the running Host, but you can type any name.
- Select **Fetch models**, then choose a model. The recommended model stays selected when the provider returns it.
- Environment values must be available to the running Host before **Fetch models** can use them.
- **Continue** stores the key or only the environment-variable name.
- Foundry Local requires downloading its model before continuing.
- A provider download failure appears in the model step. Correct the selection and retry **Download model**.
- Local GGUF can continue while its model downloads.
- Changing the provider, credential source, active credential input, or URL clears the fetched model choice.
- Installed local speech plugins start checked. Each checkbox reveals its optional model download directly below that choice.
- A missing local speech plugin is named with where to install it. The administrator installs plugins in **Settings > Plugins**.
- Identity starts with **Olivia**, **Friendly**, and a picture selected. **Finish** saves the name, picture, and persona.

> **Note:** If model settings cannot be saved, your previous credential is kept. Resolve the error, then select **Continue** again.

> **Note:** every option can be changed later. Your preferred name, your profile picture, and the main agent's name and picture are required during onboarding.

## Skip behavior

- **Skip step** leaves an optional step unsaved. Profile and Identity cannot be skipped.
- **Skip all** first takes you to Profile if its required choices are unsaved, then to Identity.
- Skipping all does not undo changes saved on earlier steps.
- **Finish** saves completion and releases due scheduled tasks on the next scheduler check.

## Related

- [Model Providers](settings.model-providers.md) - manage model connections later.
- [Speech](settings.speech.md) - finish voice setup later.
- [Your Profile](profile.md) - change your profile picture later or upload your own.
