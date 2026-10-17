# Oscova.Agents.Plugins.Echo

A minimal **sample plugin** for Oscova Agents — the canonical example of the plugin shape. It does no real work; use it as a starting template and as a fixture for verifying the plugin install flow.

## What it demonstrates

- A plugin entry point (`EchoAgentPlugin : IAgentPlugin`) wired through an `oscova-plugin.json` manifest and packaged as a NuGet plugin (`PackageType=OscovaPlugin`).
- Contributing a **toolset** — `test_echo` returns its input unchanged, while `test_pluralize` uses the plugin's ordinary `Humanizer.Core` dependency.
- Contributing a sample **model provider** (`echo`) and **speech synthesis provider** (`echo-tts`) — test doubles that make no network calls.

## How it's used

This package is not consumed via `<PackageReference>`. The Oscova Agents host installs it from NuGet through its plugin manager, restores its runtime dependencies for the current platform, and loads it after an administrator enables it and restarts Oscova. Version 0.2.0 is the first dependency-capable Echo package. See the documentation for the plugin lifecycle.

## Not for production

The echo tool and providers only return placeholder values. Use the source as a template for your own plugins.

## Links

- Website: https://oscova.ai
- Plugin authoring guide: https://oscova.ai/docs

## License

Apache-2.0

The settings contribution is an independent page in Preferences. Its Save button writes `echo-note.txt` as plaintext using the supplied current-user settings path. This demonstrates plugin-owned persistence without using the optional bound store.
