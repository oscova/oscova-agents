# Oscova LLamaSharp Provider

Runs local GGUF models through LLamaSharp.

The base edition uses CPU on Windows x64, Linux x64 and Intel macOS, and Metal on Apple Silicon. The NVIDIA CUDA edition supports Windows and Linux x64 and requires a compatible NVIDIA GPU, driver and CUDA 13 runtime. Linux also requires the OpenMP runtime (`libgomp1` on Ubuntu).

Oscova stores downloaded models in its model storage directory under `gguf`. Packaged native libraries stay in the plugin installation directory.

Install or enable the plugin, restart Oscova, then configure the **Local** model provider. No API key is required. Replacing editions requires a restart. Save a provider and select **Test** to verify inference and observed placement. CUDA initialization failures do not fall back to CPU.

## Third-party notices

This package includes native binaries from LLamaSharp and llama.cpp. Their MIT license texts are included under `licenses/`.
