# Speech

Speech controls how Oscova listens to you and speaks replies. You can use local models or configured speech providers when available.

> **In plain terms:** speech recognition turns microphone audio into text. Speech synthesis turns replies into audio.

## Configure and test

- Choose recognition and synthesis providers separately.
- Install **Oscova OpenAI** or **Oscova ElevenLabs** for hosted recognition and synthesis, or the local speech plugins, then restart.
- Recognition detects the spoken language automatically.
- Local providers require their Whisper or Kokoro download to show **Ready**.
- Install local recognition and local synthesis separately in Plugins, choosing CPU or CUDA for each capability.
- Changing editions requires a restart and keeps your providers, downloaded models, and voice selection.
- Local voice labels show the voice name, gender, and language while saving the provider voice ID.
- OpenAI providers can reuse the same saved credential as OpenAI models. Select it for each speech capability and confirm model access.
- ElevenLabs uses its saved API-key credential for both capabilities. Selecting the synthesis credential loads that account's voices before Save. Choose an accessible voice.
- If voice discovery fails, check the credential and its voice permissions, then select the credential again.
- If a provider is unavailable, enable its plugin and restart, or choose an installed provider. Your saved configuration is retained.
- Clicking either **Save speech settings** button saves all current speech drafts.
- Save before **Test Voice** because it uses the saved synthesis provider and voice.
- Hosted transcription and **Test Voice** use your provider's service credits.
- Allow microphone access in your browser for dictation and voice mode.

## Output destination

- Admins can route speech to the browser, avatar, or both, and this choice saves immediately.
- Non-admin users always receive speech in the browser.
- **Avatar only** has no browser-audio fallback.
- Enable **Speech** for your agent to request selected spoken words using your saved provider, voice, and destination.
- Explicit speech works with **Read aloud** off and leaves that button unchanged.

> **Note:** Hosted speech sends recorded audio or reply text to its configured endpoint.

> **Note:** Local **Ready** means the model file exists. CUDA also requires compatible NVIDIA software. Check application logs for initialization failures, or reinstall that capability's CPU edition and restart.

## Related

- [Dashboard](dashboard.md) - use voice controls in chat.
- [Plugins](settings.plugins.md) - install a speech edition.
