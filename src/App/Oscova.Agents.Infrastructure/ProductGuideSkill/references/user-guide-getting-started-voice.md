# Voice

Voice lets you dictate messages, hear replies and run a hands-free chat loop. Oscova Agents can use local speech models that run on your own hardware or hosted speech from OpenAI or ElevenLabs.



> **Note:**
> **ASR** means automatic speech recognition: turning microphone audio into text. **TTS** means text-to-speech: turning agent text into spoken audio.

## Choose speech providers

1. Install a speech plugin. Interactive `oscova start` lists OpenAI in its optional-plugin checklist, unchecked by default. You can also install `Oscova.Agents.Plugins.OpenAI` or another speech plugin through [Plugins](references/user-guide-settings-and-admin-plugins.md), enable it and restart.
2. Open **Speech** settings and pick a provider for recognition and another for synthesis. The selectors are per user and available to every role.
3. Select **Save speech settings**. Either save button saves all current speech drafts.

Save before using **Test Voice**, because the test uses the saved synthesis provider and voice. Changing the synthesis draft previews that provider's voice list without saving it.

- If no speech plugin is enabled, Speech settings shows installation guidance.
- A disabled provider keeps your saved choices and reports unavailability until its plugin is enabled again or you choose another installed provider.
- Local providers show download cards for the required models. Hosted OpenAI providers show credential and model fields, and OpenAI synthesis also shows a voice selector.
- To reuse one OpenAI key, select the same saved credential for recognition, synthesis and your OpenAI model configuration. Each selection is independent, and OpenAI model support works without the speech plugin.

## Download local models

Use local speech when you want voice features on your own hardware.

- Local recognition uses the Whisper model download and detects the spoken language automatically.
- Local synthesis uses the Kokoro model download and enables spoken replies and voice preview.
- Download cards show progress and can cancel active downloads. **Ready** appears after each model is installed.

The model files are not bundled, so downloading them makes an outbound network request from the host. The voice selector and **Test Voice** stay disabled until the saved synthesis provider is ready.

### NVIDIA local speech

Local recognition and synthesis are separate optional plugins:

- `Oscova.Agents.Plugins.LocalSpeechRecognition` supplies Whisper and supports Windows x64, Linux x64, Intel Mac and Apple Silicon.
- `Oscova.Agents.Plugins.LocalSpeechSynthesis` supplies Kokoro and currently requires Apple Silicon on macOS.

Install the capabilities you need through [Plugins](references/user-guide-settings-and-admin-plugins.md), enable them, then restart before selecting their local providers. At startup, each selected local speech capability offers one **CPU / NVIDIA CUDA** choice on Windows x64 and Linux x64. CPU is selected first, recognition and synthesis can use different editions, and macOS uses CPU without this question.

NVIDIA acceleration uses `Oscova.Agents.Plugins.LocalSpeechRecognition.Cuda` and `Oscova.Agents.Plugins.LocalSpeechSynthesis.Cuda` on Windows x64 or Linux x64. Each is a separate edition of its own capability and keeps its models and settings. Prepare these prerequisites for the Oscova host process:

- A compatible NVIDIA driver and NVIDIA GPU.
- For recognition, CUDA 13 compatible with Whisper's 13.0.1 requirement.
- For synthesis, CUDA 13 and cuDNN 9 for ONNX Runtime 1.31.

Oscova does not install the driver, toolkit or cuDNN. Installing a package checks its structure and platform assets. **Active** means the plugin loaded and speech **Ready** means its model file exists, but neither confirms that CUDA can initialize. Initialization failures leave details in application logs. Install the CPU edition and restart to recover.

The package uses GPU device zero, and individual unsupported ONNX operators may run on CPU. Kokoro keeps separate sessions per user, so GPU memory use grows with active speech sessions. AMD or Intel GPU acceleration, accelerated macOS synthesis, GPU selection, automatic runtime rollback and switching without restart are not available.

## Configure OpenAI speech

The default OpenAI models are `gpt-4o-mini-transcribe` for recognition and `gpt-4o-mini-tts` for synthesis. Edit the model fields before saving if your account or deployment uses different model IDs.

OpenAI speech counts as available when the linked credential has a stored value. Transcription or synthesis can still fail if the key, model, account access or endpoint is invalid. OpenAI recognition sends recorded audio to the configured endpoint and OpenAI synthesis sends reply text, while local Whisper and Kokoro process those inputs on the Oscova host.

## Configure ElevenLabs

1. Install and enable **Oscova ElevenLabs** in [Plugins](references/user-guide-settings-and-admin-plugins.md), then restart.
2. Add its API-key credential in Credentials as a saved secret. The operator can also use an environment reference such as `ELEVENLABS_API_KEY`.
3. Select ElevenLabs and that credential independently for recognition and synthesis.
4. Choose a voice from the list loaded for the synthesis credential, then select **Save speech settings**.

Recognition defaults to `scribe_v2`, with `scribe_v1` also supported. Synthesis defaults to `eleven_flash_v2_5`. An empty voice list means no voices were returned. If discovery fails, check the selected credential and its voice permissions, then select the credential again.

A populated credential does not verify account permissions or available credits. **Test Voice** consumes synthesis credits, and recognition sends the recorded audio to ElevenLabs and consumes transcription credits.

## Choose and test a voice

1. Pick a voice. For local synthesis the dropdown is disabled until synthesis is ready and voices have loaded. Each local choice shows the voice name, gender and language, while Oscova saves its provider voice ID. OpenAI synthesis offers the built-in OpenAI voice list.
2. Enter **Sample text** and select **Test Voice**. The button activates only when synthesis is ready and the sample text is not empty.
3. Select **Save speech settings** to make the voice the saved voice for future replies.

## Use voice in chat

The dashboard has three voice controls near the input:

- The microphone dictates one message and requires speech recognition.
- The speaker reads agent replies aloud and requires speech synthesis.
- Voice mode starts a hands-free listen-and-speak loop and requires both services.

> **Important:**
> If these controls are greyed out, return to Speech settings and confirm recognition and synthesis are ready.

Your browser must grant microphone access. A single push-to-talk recording is limited to 20 MB when it is transferred from the browser.

## Speak selected words

Enable the **Speech** tool for your agent and save a working synthesis provider and voice in Speech settings. You can then ask for a detailed written answer and a separate spoken sentence, for example: "Explain this fully, then speak just the main takeaway."

- The tool speaks the text the directly selected chat agent supplies. Delegated agents cannot deliver speech to your chat.
- It works even with **Read aloud** off and leaves that button unchanged. If **Read aloud** is on, the written reply can also be read automatically.
- Explicit speech uses your saved Speech voice and output destination. Avatar controls use the operator's saved Speech provider and voice.
- The Speech tool requires an active web chat and follows local-only restrictions.
- Synthesis runs on the Oscova host or configured provider, so a phone browser can play the prepared audio too. If the browser blocks playback, tap the page to resume the queued clip. A prepared clip does not confirm audible playback, and reopening a conversation does not replay earlier speech.

## Avatar speech output

Admins can choose **Browser only**, **Browser and avatar** or **Avatar only**. The selector is visible only to admins and saves immediately. Avatar routing also requires the separate Oscova Avatar Engine to be configured and reachable, and non-admin users always receive speech in the browser.

**Browser and avatar** keeps browser playback if avatar delivery becomes unavailable. **Avatar only** has no browser-audio fallback, so confirm the Engine is ready before selecting it.

## Troubleshooting

- If the microphone button is disabled, confirm the selected recognition provider is available.
- If spoken replies or **Test Voice** are disabled, confirm the selected synthesis provider is available.
- If local speech is selected, download the corresponding model and wait for the card to show **Ready**.
- If OpenAI speech fails, confirm the saved credential has a value and the model ID is valid.
- If recording will not start, allow microphone access in the browser and retry.
- If a draft sounds unchanged, save it before testing. **Test Voice** uses the saved provider.
- If avatar-only output is silent, confirm the Engine is running and reachable from Avatar settings.

## Related articles

- [Main agent chat](references/user-guide-chat-and-agents-main-agent-chat.md)
- [Model providers](references/user-guide-concepts-model-providers.md)
- [Connect the Engine](references/avatar-engine-connecting.md)
