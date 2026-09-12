# Oscova Agents

Oscova Agents is a reusable .NET 10 framework for building local-first agents and plugins on the Microsoft Agent Framework.

## Projects

- [Oscova.Agents](src/Oscova.Agents/README.md), the core agent framework and plugin contract.
- [Web](src/Oscova.Agents.Plugins.Web/README.md), web and news search tools.
- [Foundry Local](src/Oscova.Agents.Plugins.FoundryLocal/README.md), local model discovery and inference.
- [LLamaSharp](src/Oscova.Agents.Plugins.LlamaSharp/README.md), local GGUF inference.
- [Local Speech](src/Oscova.Agents.Plugins.LocalSpeech/README.md), Whisper recognition and Kokoro synthesis.

## Build

```shell
dotnet build Oscova.Agents.sln -c Release
```

Each project README contains its package-specific setup and usage details.
