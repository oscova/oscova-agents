# Oscova Local Speech Recognition

Provides local Whisper speech recognition. Its CPU edition supports Windows x64, Linux x64, Intel Mac and Apple Silicon.

Install or enable the plugin, restart Oscova, then download the Whisper model from onboarding or Speech Settings. No API key is required. Whisper detects the spoken language automatically. Models live in the existing models directory, independently of packaged native libraries.

## NVIDIA CUDA edition

`Oscova.Agents.Plugins.LocalSpeechRecognition.Cuda` targets Windows x64 and Linux x64. The host needs an NVIDIA GPU, driver, and [CUDA 13 compatible with Whisper's 13.0.1 requirement](https://github.com/sandrohanea/whisper.net/tree/1.9.1#cuda-support). Prerequisite libraries must be discoverable by the host process. Oscova does not install them. GPU inference in this split package still needs hardware verification.

Use **Settings > Plugins > Find plugins online**, install the desired CPU or CUDA package, and restart the host. Both editions use plugin ID `oscova.local-speech-recognition`. Models and activation settings are preserved. An installed CUDA edition satisfies startup reconciliation. Finish a pending replacement before selecting another edition.

To recover from a CUDA initialization failure, install the CPU package and restart. There is no automatic runtime rollback or native hot swap. Plugin **Active** and model **Ready** indicators do not certify GPU readiness. Initialization failures retain prerequisite guidance in application logs.

Whisper CUDA binaries ship as ordinary dependencies `Oscova.Agents.Native.Whisper.Cuda.Windows` and `.Linux`. Restore downloads both archives and installs current-platform native assets. This package has no Kokoro or ONNX dependency. Models and NVIDIA prerequisites are separate downloads.

## Build locally

Run `packaging/Pack.ps1` to build the CPU and CUDA archives plus both native dependencies under the repository's `artifacts/nuget` directory. CUDA compilation uses separate SDK intermediates.

## Third-party notices

This package includes native binaries from Whisper.net and whisper.cpp. Their MIT license texts are included under `licenses/`.
