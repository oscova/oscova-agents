# Oscova Local Speech

Adds CPU-only local speech recognition through Whisper and speech synthesis through Kokoro.

The plugin supports Windows x64, Linux x64, Intel macOS, and Apple Silicon macOS. Oscova stores downloaded models in its existing `app_data/models` directory, while the plugin keeps its packaged native libraries and voices in the read-only plugin installation directory.

Install or enable the plugin, restart Oscova, then download the speech models from onboarding or Speech Settings. No API key is required.

Whisper detects the spoken language automatically. Kokoro voice choices show the voice name, gender, and language while Oscova saves the underlying provider voice ID.

## Third-party notices

This package includes native binaries from Whisper.net and whisper.cpp. Their MIT license texts are included under `licenses/`.
