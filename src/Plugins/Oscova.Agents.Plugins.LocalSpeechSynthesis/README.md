# Oscova Local Speech Synthesis

Provides local Kokoro speech synthesis. The current native runtime supports Windows x64, Linux x64 and Apple Silicon.

Install or enable the plugin, restart Oscova, then download the Kokoro model from onboarding or Speech Settings. No API key is required. Voices are packaged with the provider, and saved voice selections use their underlying provider IDs.

## NVIDIA CUDA edition

`Oscova.Agents.Plugins.LocalSpeechSynthesis.Cuda` targets Windows x64 and Linux x64. The host needs an NVIDIA GPU, driver, CUDA 13, and [cuDNN 9 for ONNX Runtime 1.31](https://onnxruntime.ai/docs/execution-providers/CUDA-ExecutionProvider.html#requirements). Prerequisite libraries must be discoverable by the host process. Oscova does not install them. GPU inference in this split package still needs hardware verification.

Use **Settings > Plugins > Find plugins online**, install the desired CPU or CUDA package, and restart the host. Both editions use plugin ID `oscova.local-speech-synthesis`. Models, activation, and voice settings are preserved. An installed CUDA edition satisfies startup reconciliation.

Only one replacement can wait for restart. Finish it before selecting another edition. To recover from a CUDA initialization failure, install the CPU package and restart. There is no automatic runtime rollback or native hot swap.

CUDA initialization failures retain prerequisite guidance in application logs. Plugin **Active** and model **Ready** indicators do not certify GPU readiness. Device zero is used. Some ONNX operators can still run on CPU, and per-user Kokoro sessions each consume GPU memory.

This package contains no Whisper or NAudio dependency. Its download includes the ONNX dependency graph and excludes separately downloaded models and NVIDIA prerequisites. The current CPU native runtime requires Apple Silicon on macOS.

## Build locally

Run `packaging/Pack.ps1` to build the CPU and CUDA archives under the repository's `artifacts/nuget` directory. CUDA compilation uses separate SDK intermediates.
