# Oscova Local Speech

Adds local speech recognition through Whisper and speech synthesis through Kokoro.

The default CPU package, `Oscova.Agents.Plugins.LocalSpeech`, supports Windows x64, Linux x64, and Apple Silicon macOS. Oscova stores downloaded models in its existing `app_data/models` directory, while the plugin keeps packaged native libraries and voices in the read-only plugin installation directory.

Install or enable the plugin, restart Oscova, then download the speech models from onboarding or Speech Settings. No API key is required.

Whisper detects the spoken language automatically. Kokoro voice choices show the voice name, gender, and language while Oscova saves the underlying provider voice ID.

## NVIDIA CUDA edition

`Oscova.Agents.Plugins.LocalSpeech.Cuda` is a separate 1.0.4 package built from the same project. It targets Windows x64 and Linux x64. End-to-end GPU inference has been validated on Windows x64. Linux x64 validation is pending. macOS is excluded. The operator needs an NVIDIA GPU and driver, [CUDA 13 compatible with Whisper's 13.0.1 requirement](https://github.com/sandrohanea/whisper.net/tree/1.9.1#cuda-support), and [cuDNN 9 for ONNX Runtime 1.29](https://onnxruntime.ai/docs/execution-providers/CUDA-ExecutionProvider.html#requirements). Prerequisite libraries must be discoverable by the host process. Oscova does not install them.

Startup keeps installing CPU by default, without a GPU prompt. To switch, use **Settings > Plugins > Find plugins online**, install the CUDA package, and restart the host process. Both editions use plugin ID `oscova.local-speech` and provider key `local`. The pending name appears under **After restart**. Models, activation, and voice settings are preserved. An existing CUDA installation satisfies the startup default check.

Only one replacement can wait for restart. Finish it before selecting another edition. To recover from a CUDA initialization failure, install `Oscova.Agents.Plugins.LocalSpeech` and restart. There is no automatic runtime rollback or native hot swap.

CUDA initializes explicitly and failures retain prerequisite guidance in application logs. Plugin **Active** and model **Ready** indicators do not certify GPU readiness. Device zero is used. Some ONNX operators can still run on CPU. Existing per-user Kokoro sessions each consume GPU memory.

Whisper CUDA binaries ship as ordinary dependencies `Oscova.Agents.Native.Whisper.Cuda.Windows` and `.Linux`, both version 1.9.1. Restore downloads both platforms' archives and the ONNX GPU dependencies, then installs only current-platform native assets. The small plugin archive alone does not represent the complete download.

Measured with version 1.0.3 on Windows x64, the complete package graph is about 286 MiB downloaded and 167 MiB installed for CPU, or 726 MiB downloaded and 482 MiB installed for CUDA. These figures exclude speech models, separately installed NVIDIA prerequisites, and temporary extraction space.

## Build both editions locally

Run `pwsh -File ./src/Oscova.Agents.Plugins.LocalSpeech/packaging/Pack.ps1` from the repository root. It produces CPU and CUDA 1.0.4 archives plus both native 1.9.1 dependencies under `artifacts/local-speech/packages`. `-OutputDirectory` selects another destination. The CPU package is also retained under the project's `bin/Release` directory. CUDA restore and compilation use separate SDK artifacts. This script does not publish packages.

## Third-party notices

This package includes native binaries from Whisper.net and whisper.cpp. Their MIT license texts are included under `licenses/`.
