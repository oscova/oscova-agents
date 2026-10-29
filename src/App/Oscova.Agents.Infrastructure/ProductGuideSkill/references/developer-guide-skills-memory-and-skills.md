# Memory and skills

Memory and skills give agents durable capability without putting every possible instruction into the base system prompt. Memory supplies user-specific context. Skills supply reusable knowledge, reference files, assets, and optional script-backed procedures.



> **Note:**
> **Progressive disclosure** means loading detailed guidance only when the agent needs it instead of putting everything into the starting prompt.

> **Note:**
> **Skill** means a folder with a `SKILL.md` file that declares a name and description, plus optional resources or scripts the model can load on demand.

## Runtime boundaries

Oscova assembles active-agent context in this order: environment, ordinary-tool exposure, the agent's standing instruction, browser guidance when applicable, private notes for non-shared agents, exposed agent tools, assigned skills, memory, Library, task list, and request-specific providers. Providers that are not applicable are omitted.

The standing instruction, memory prompt, skills prompt, and compaction prompt have separate responsibilities:

- The agent's system instruction is rendered with user, time, agent, and shared-agent personal-context variables.
- `prompts/memory-system.md` wraps the current persistent-memory snapshot.
- `prompts/skills.md` advertises assigned skills and must retain the framework's `{skills}` placeholder.
- `prompts/compaction.md` tells the chat reducer how to summarize older conversation messages.

Compaction does not replace persistent memory or skill disclosure. It preserves system messages and reduces non-system conversation history only when the configured history budget is crossed.

Memory and skills are added only when the definition is the active requested agent. A definition invoked through agent-as-tool keeps its instruction, ordinary tools, workspace policy, model, and compaction setting, but it does not receive memory or skills during that delegated build. Running the same definition directly makes it active and restores its configured memory and skills.

## Persistent-memory contract

`IAgentMemoryService` binds the user's encrypted memory bundle and dated logs. Its `GetStore(userId)` creates an `EncryptedKnowledgeStore` bound to that user's root. Private agent notes use the same class with their own authorized root.

| Memory kind | Operations | Runtime use |
|---|---|---|
| Long-term concepts | Find, read, save and confirmed forget | Complete root index injected, detailed concepts and nested indexes read on demand. |
| Short-term daily logs | Read a local date and append to today | Complete today and yesterday logs injected. |
| Private behavioral lessons | Find, read and save | Complete private entry index injected for the directly running private agent. Ordinary chats and Reflection save lessons. |
| Conversation memory | Search and get indexed excerpts | Host-owned encrypted vector recall uses the trusted running agent identity and an embedding provider. |

The decrypted format follows Open Knowledge Format v0.2: YAML frontmatter with a nonempty string `type`, followed by Markdown. Concept IDs are bundle-relative paths without `.md`. The agent chooses new IDs explicitly. Save returns the canonical ID and logical Markdown reference. Returned references start at the bundle root, such as `/design/details.md`, so copied links work in nested concepts. Existing IDs come from persisted indexes or find results each run.

Frontmatter is an extensible metadata object. Title and description improve discovery. Updates preserve unsupplied top-level fields, including their nested values. Agent writes cannot supply `generated` or `verified`. The runtime records its actual writer and write time without claiming verification.

Files use `.md.enc` at rest through the existing user-vault protection and backup policy. Root and nested `index.md.enc` files are derived from saved concepts. Only the root declares `okf_version: "0.2"`. Markdown links disclose details progressively. Reads resolve contained references and report missing targets. They do not fetch external links or recursively load linked bodies. Files edits use the same validation and index owner. Edit concepts to rebuild derived indexes.

`AgentMemoryService` receives user-root resolution, the relative memory root, the maximum UTF-8 size, local-date resolution, backups and content protection. `AppendDailyLogAsync` uses its supplied time zone and defaults to UTC. Daily files declare `type: Daily Memory` and retain their complete accumulated body.

`MemoryContextProvider` renders the complete entry index, today and yesterday into `<agent_memory>`. Private notes inject their complete entry index. There is no provider token cap. The model's context window and the existing 524288 UTF-8 bytes per complete document still apply.

- `memory__find(query)` searches saved titles, descriptions, tags and bodies lexically, without embeddings or a result-count cap.
- `memory__read(document?)` reads the entry index by default, or one discovered concept or router.
- `memory__save(conceptId, frontmatter, content)` saves one durable concept.
- `memory__save_daily(content)` appends short-term information to today.
- `memory__read_daily(date?)` reads today by default, or a `yyyy-MM-dd` local date.
- `memory__forget(conceptId)` requires Boolean confirmation, backs up and deletes one concept, rebuilds indexes and reports surviving inbound links. It retains linked detail and normal backup history.
- `memory__search(query, maxResults)` and `memory__get(threadId)` retrieve indexed conversation excerpts. Search defaults to 5 results. Values below 1 use 5, values above 50 use 50.

Private `notes__find`, `notes__read` and `notes__save` use the same concept format for evidenced behavioral lessons. Find/read existing lessons before updates and skip unchanged duplicates. User facts and preferences belong in user memory.

Daily Memory exposes save_daily only. Dreaming exposes memory find, read, read_daily and save. Reflection exposes notes find, read and save. Vector Indexing retains its deterministic owner.

## Skill disclosure

Skills are assigned to an agent by name. At runtime, Oscova builds Microsoft Agent Framework's `AgentSkillsProvider` from the user's skills directory, applies `SkillFileDiscoveryOptions`, then filters the source case-insensitively to the definition's assigned names.

The provider implements progressive disclosure in this order:

1. It advertises available names and descriptions, ordered by ordinal skill name, in the starting instructions.
2. `load_skill` accepts an exact skill name and returns the complete `SKILL.md` plus authoritative `<available_resources>` and `<available_scripts>` lists.
3. `read_skill_resource` accepts the exact skill and relative resource names and returns that resource's UTF-8 text.
4. `run_skill_script` accepts exact skill and script names plus an optional JSON array of string arguments.

Oscova disables approval for the two read-only tools, `load_skill` and `read_skill_resource`. `run_skill_script` remains approval-required. Its approval covers the whole script invocation. Commands inside the script are not approved separately. All three functions are present when the provider has at least one assigned skill. A skill with no scripts advertises an empty scripts list rather than making up a script name.

The operator's installation-wide execution selection controls both ordinary shell commands and file-skill scripts for every account. Host is the default and uses the operating-system account running Oscova or the selected project's device. Installed execution plugins can supply other environments, including Microsoft MXC and Docker. Disabled exposes no script extensions or shell execution. A run snapshots this choice, and a later setting change makes subsequent shell launches in that run fail instead of switching targets.

Host execution is rejected during Offline or agent-local-only runs because it cannot enforce network isolation. Scripts require an interpreter and invocation supported by the selected environment. For Docker, put the interpreter and dependencies in the image and use a shebang such as `#!/usr/bin/env python3` for an executable script. Tools installed on the host are not automatically present in that image.

Blank or unknown skill/resource/script names return model-readable `Error:` results. Resource-read failures and script failures are logged and propagate unless detailed script errors were deliberately enabled. Oscova leaves detailed skill errors disabled. Cancellation tokens flow through loads, reads, and script execution.

The framework builder caches the resolved skill list for the life of the built provider and deduplicates names with first occurrence winning. Import and settings flows invalidate cached agents so a later build sees filesystem changes.

## File discovery and validation

`SkillDiscoverer` is the app's settings-time and import-time metadata authority. It searches the skills root and two child levels for `SKILL.md`, then requires:

- YAML frontmatter delimited by `---`.
- A lowercase letter/digit/hyphen name no longer than 64 characters, with no leading, trailing, or consecutive hyphens.
- A description no longer than 1,024 characters.
- A name that exactly matches the parent directory.

Candidate directories are sorted case-insensitively before parsing. Duplicate names therefore keep the first alphabetical directory, and the returned list is ordered by name.

Runtime file discovery uses the framework source with Oscova's narrower filters:

- Resources: `.md`, `.json`, `.yaml`, `.yml`, `.csv`, `.xml`, and `.txt`, only as direct children of `references/` or `assets/`.
- Scripts: only direct children of `scripts/`. Disabled mode exposes no script extensions. Enabled modes use the framework defaults `.py`, `.js`, `.sh`, `.ps1`, `.cs`, and `.csx`.
- Search depth: two, enough for the skill root and one support-directory level without exposing an arbitrary deep tree.

The file source normalizes relative paths and skips files that escape the skill directory, traverse through symlinks/reparse points, use disallowed extensions, or fail the configured directory filters. The configured skills root is trusted. Skill bodies, names, and descriptions are still inserted into model context as supplied. Treat imported skills as instruction-bearing code, and never enable script execution for an untrusted package.

## Import and agent-authored writes

The framework-owned `ISkillImportService` contract and `SkillImportService` implementation accept one raw `.md` file or one `.zip` package beneath a caller-selected per-user skills root. Uploads are limited to 10 MB and expanded zip content to 30 MB. Archives are extracted through a containment-checked plan and must contain exactly one skill, either at the archive root or in one wrapper directory.

Import stages content beside the selected skills root, rejects path-shaped skill names, unsupported resource extensions, reserved Oscova provenance from ordinary imports, invalid metadata, and installed-name conflicts. `SkillImportResult` reports neutral compatibility facts for extra top-level paths and recognized frontmatter such as `allowed-tools`, `user-invocable`, and `disable-model-invocation`. The Host maps those facts into application warning sentences without changing the agent's tool policy.

Agent-authored creation and update use the same import pipeline instead of raw filesystem tools. The approval-gated authoring path may stamp trusted Oscova provenance. Updates are limited to skills with that provenance. Registry-installed, user-imported, and manually authored skills are rejected.

> **Tip:**
> Use memory for user-specific facts and decisions. Use skills for reusable procedures, reference material, and domain knowledge that should be assigned deliberately.

## Host-visible behavior

The host exposes different views of these systems:

- Agent settings show assigned skills and the memory enable switch.
- Skills settings show installed packages, sharing, import validation, and import warnings. Execution settings separately select the environment for commands and skill scripts.
- Prompt settings edit memory, skills, and compaction instructions independently.
- Chat tool activity shows memory and skill function calls and their returned evidence.
- Chat Memory browses indexed conversation chunks only. It is not a browser for long-term concepts or daily-log files.

If no assigned skills resolve, the framework provider contributes neither skill instructions nor skill tools. Invalid filesystem skills are skipped with logs during discovery, while explicit imports fail with a validation response. Memory search failures return a short host-safe message. Cancellation still propagates.

## Related articles

- [Context Providers](references/developer-guide-runtime-context-providers.md)
- [Skills](references/user-guide-knowledge-and-files-skills.md)
- [Chat Memory](references/user-guide-chat-and-agents-chat-memory.md)
- IAgentMemoryService
- AgentMemoryService
- MemoryContextProvider
- SkillDiscoverer
- ISkillImportService
- SkillImportService
- SkillImportResult
- SkillFileDiscoveryOptions
