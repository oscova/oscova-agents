# Oscova LLamaSharp Provider

Adds CPU-only local GGUF inference to Oscova through LLamaSharp.

The plugin supports Windows x64, Linux x64, and Apple Silicon macOS. Oscova stores downloaded models in its `app_data/models/gguf` directory; the plugin keeps its packaged native libraries in the read-only plugin installation directory.

Install or enable the plugin, restart Oscova, then configure the **Local** model provider. No API key is required.

## Third-party notices

This package includes native binaries from LLamaSharp and llama.cpp. Their MIT license texts are included under `licenses/`.
