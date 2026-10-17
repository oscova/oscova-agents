# Oscova.Agents

Oscova Agents is a reusable .NET agent framework for building **local-first** AI agents and tools on top of the Microsoft Agent Framework (`Microsoft.Agents.AI`). It is the foundation the rest of the Oscova Agents product builds on.

- Build AI agents with pluggable model providers, toolsets, and context providers.
- Extend agents through a NuGet-distributed plugin system (see the [Echo plugin example](https://oscova.ai/docs/articles/developer-guide/examples/echo-plugin.html)).
- Targets .NET 10.

## Install

```
dotnet add package Oscova.Agents
```

## Settings

Derive a settings domain from `SettingsBase` and persist it through `IAppSettingsRepository`. Use `FileSettingsRepository` when each settings key should be stored beneath a directory selected by the caller.

## Task catalogue

`AgentTaskCatalog` scans `tasks/<slug>/task.md` files beneath a caller-selected users root. Callers may restrict accepted user IDs and identify task folder names that map to built-in task handlers. `AgentTaskRepository` persists definitions and split run artifacts beneath that caller-selected root. Callers provide reserved task-folder keys and may supply an `IRedactionService` for persisted run content.

## External task triggers

Use `TaskExternalTriggerValidator` to validate supplied values against a task's declared external parameters, then use `TaskInstructionRenderer` to apply those values to the task instruction. Validation returns a read-only dictionary and exposes typed failure metadata so the embedding host can own authentication, authorization, endpoint responses, scheduling, and execution.

## Task run observation

Use `AgentTaskRunObserver` with the agent-run observer middleware to project the first current-run user message, eligible tool-result messages, non-streaming responses, and the first assembled instruction into an `AgentTaskRun`. Create one observer per run; the embedding executor owns attachment, clearing, execution, persistence, and presentation.

## Persistence primitives

Use `IRedactionService` to keep persistence code provider-neutral and `FileSessionIndexCoordinator` to serialize rewrites of the same file index.

## File backups

`UserFileBackupService` captures bounded text restore points beneath caller-selected user roots. Callers provide user-root resolution, supported-file policy, and the maximum file size.

## Agent memory

`AgentMemoryService` binds long-term concepts and dated short-term logs to caller-selected user roots. `EncryptedKnowledgeStore` shares encrypted OKF v0.2 persistence, metadata-preserving updates, lexical discovery and derived directory indexes with private agent notes. Callers supply the relative root, UTF-8 file limit, local date, vault protection and backups. Entry-index injection has no provider token cap.

## Shared agent personal context

`ISharedAgentPersonalContextRepository` defines per-user context layered over a shared agent definition. `SharedAgentPersonalContextRepository` stores `agent_id` frontmatter and content as `<slug>/context.md` beneath an exact root selected by the host. The host supplies that per-user root and the maximum UTF-8 text size while retaining authorization and user-directory topology.

## Skill import

`ISkillImportService` defines safe markdown and zip skill import, and `SkillImportService` installs validated skills beneath caller-selected per-user roots. `SkillImportResult` returns imported metadata and neutral compatibility facts so each host can choose its own warning wording.

## Plugins

Use `AgentPluginBuilder` to register toolset, model, and speech provider factories that receive an `AgentPluginContext`. Provider factories can use host-managed HTTP clients and logging, while selected user API keys continue to arrive through provider operation parameters. User-scoped toolset contexts resolve only credential keys declared directly by that plugin's registered contributions.

## Speech routing

`SpeechRecognitionRouter` and `SpeechSynthesisRouter` select the exact registered provider whose `Type` matches the supplied `SpeechProviderConfig`. The host selects that configuration and supplies credentials, offline policy, download URLs, and session identifiers. The routers enforce offline endpoint policy before invoking a provider.

`SpeechProviderKeys` contains well-known identifiers, not a complete allowlist. A plugin-defined provider type routes without a framework constant when the provider is registered.

## Avatar Engine bridge

Use `IAvatarBridgeService` and the types under `Oscova.Agents.Avatar.Contracts` to integrate with an Oscova Avatar Engine. See the [Engine HTTP API](https://oscova.ai/docs/articles/avatar-engine/http-api.html) for routes, authentication, and JSON shapes.

## Conversations

Use the conversation models and `IChatThreadRepository` contract for persisted chat history and framework session state. `ChatChannels` provides the stable channel names stored with conversation records.

`ChatThreadRepository` provides file-backed per-user conversation persistence beneath a caller-selected users root.

`ThreadRunGuard` coordinates one active run for each exact user and case-insensitive thread identity, including waiting admission, targeted cancellation, and user-wide quiescence.

## Links

- Website: https://oscova.ai
- Documentation: https://oscova.ai/docs

## License

Apache-2.0
