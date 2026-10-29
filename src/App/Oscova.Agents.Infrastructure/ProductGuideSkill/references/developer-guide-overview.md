# Developer guide

Use this guide to build trusted .NET plugins or extend the reusable `Oscova.Agents` framework. If you want to configure and operate the application instead, start with the [User Guide](references/user-guide-overview.md).

> **Note:**
> A plugin runs inside the Oscova application process and is not sandboxed. Install and develop only code you trust.

## Choose an extension path

| Goal | Start here | Continue with |
| --- | --- | --- |
| Build a plugin | [Build Your First Plugin](references/developer-guide-plugins-first-plugin.md) | [Manifest](references/developer-guide-plugins-manifest.md), [IAgentPlugin](references/developer-guide-plugins-iagent-plugin.md), [Plugin Builder](references/developer-guide-plugins-plugin-builder.md), [Plugin Context](references/developer-guide-plugins-context.md), and [Credentials](references/developer-guide-plugins-credentials.md) |
| Add plugin-owned settings | [Compiled plugin settings](references/developer-guide-plugins-compiled-settings.md) | Independent pages, speech and execution panels, authenticated capabilities, and storage ownership |
| Add or expose tools safely | [Toolsets](references/developer-guide-toolsets-toolsets.md) | [Tool Policies](references/developer-guide-governance-tool-policies.md) and [Workspace Path Safety](references/developer-guide-governance-workspace-path-safety.md) |
| Extend agent construction or invocation | [Agent Runtime](references/developer-guide-runtime-agent-runtime.md) | [Context Providers](references/developer-guide-runtime-context-providers.md), [Runtime Hooks](references/developer-guide-runtime-hooks.md), and [Agent as Tool](references/developer-guide-runtime-agent-as-tool.md) |
| Work with persistent context and reusable instructions | [Memory and Skills](references/developer-guide-skills-memory-and-skills.md) | Follow its memory, progressive disclosure, resource, and script boundaries |
| Persist conversations and coordinate active runs | [Conversation Persistence](references/developer-guide-runtime-conversation-persistence.md) | Separate display transcripts, agent sessions, encrypted storage, and incognito state |
| Learn from executable examples | [Echo Plugin](references/developer-guide-examples-echo-plugin.md) | [Testing Plugins](references/developer-guide-examples-testing-plugins.md) |
| Look up exact public signatures | API Reference | The generated reference covers the `Oscova.Agents` assembly only |

## Know the supported boundary

The supported reusable surface lives in the `Oscova.Agents` project. The generated API reference covers that assembly and its public contracts.

`Oscova.Agents.Core`, `Oscova.Agents.Infrastructure`, and `Oscova.Agents.Host` contain product-specific contracts, persistence, services, UI, authentication, and transport. They are not included in the generated public API reference. This guide mentions host behavior only when it explains an observable boundary of a supported reusable contract.

## Work source-first

Start with the conceptual article for the extension point, then use the generated API reference for exact signatures. Verify behavior against the live public source and focused tests before relying on implementation details. For Microsoft Agent Framework types, use the dependency version and source that match the Oscova build.
