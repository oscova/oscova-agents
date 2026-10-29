# Oscova

The `oscova` command installs, starts and manages Oscova, a self-hosted AI agent OS. Chats, memory, instructions and settings are encrypted on disk under a Vault Password only you hold. It runs on Windows, macOS and Linux with local models such as Ollama, and needs the .NET 10 SDK.

```bash
dotnet tool install --global Oscova.Agents.App
oscova start
```

Update with `dotnet tool update --global Oscova.Agents.App`. Install the Windows Avatar Engine with `oscova avatar install`.

Oscova is free and open source. Each version is distributed under its license agreement until its source is published on the `app` branch on GitHub. From then on, AGPL-3.0 applies to that version. The agent framework and official plugins are open source under Apache-2.0.

[Website](https://oscova.ai) · [Documentation](https://oscova.ai/docs/) · [Framework and plugins](https://github.com/oscova/oscova-agents)
