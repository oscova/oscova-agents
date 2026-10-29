# Skills

A skill is a reusable package, usually a `SKILL.md` file with optional support folders, that an agent loads when it needs that capability. Use a skill when a workflow is too detailed for one system instruction but should not be built into every agent.



## Import a skill

1. Open Skills and select **Import Skill**.
2. Choose a raw `SKILL.md` file or a `.zip` package up to 10 MB. Other file types are rejected before upload.
3. Assign the skill to an agent in the agent editor before expecting that agent to use it.

Imported skills appear in **Installed Skills** and are stored in your user workspace. Duplicate skill names are rejected, so an import cannot silently overwrite an installed skill. If an import succeeds with warnings, the dialog keeps them visible before it closes.

Zip imports are also limited to 30 MB after expansion. A package may upload within 10 MB and still fail validation when its uncompressed content is too large or unsafe.

To uninstall a skill, use the delete icon in **Installed Skills**. Uninstalling removes the skill directory and the skill name from every agent allowed-skill list. If uninstall fails, correct the reported problem and retry. Bundled skills have no delete icon.

`oscova-agents-guide` is the built-in product guide assigned to the main agent. Oscova restores its packaged files and main-agent assignment at startup, so local edits, deletion or unassignment are replaced by the guide shipped with the installed release. `visualize` and `oscova-research` are also bundled and restored the same way. `oscova-research` explains the `/research` chat command and is kept assigned to the main agent like the guide. Other skills and assignments are left unchanged.

### Supported skill contents

Oscova discovers a skill from a valid `SKILL.md` file. Discovery searches two levels deep, so keep packages simple: one skill folder, one `SKILL.md` and predictable support folders. A package may also include:

- `scripts/` for executable scripts.
- `references/` for readable support documents.
- `assets/` for readable support files.

Limits:

- Readable resources are limited to `.md`, `.json`, `.yaml`, `.yml`, `.csv`, `.xml` and `.txt`, and only as direct children of `references/` and `assets/`. Files nested deeper are not exposed as skill resources.
- Bundled scripts are discovered only as direct children of `scripts/`.
- Approving a skill script runs the whole script. Commands inside it are not approved separately.

### Where scripts run

[Execution settings](references/user-guide-settings-and-admin-execution.md) select the environment for shell commands and bundled skill scripts.

- **Docker** invokes `.py`, `.js` and `.sh` scripts directly instead of selecting an interpreter from the file extension. Give each script a shebang, a first line beginning with `#!`, for Python, Node or the shell installed in the sandbox image.
- **Host** runs files through the configured host shell and requires the matching runtime on `PATH`.

## Share skills with users

1. Turn on **Share with users** beside an installed skill. Oscova copies the published package into each recipient's own skills folder, including accounts initialized later. Recipients see **Managed by operator** on managed copies and never read the operator's private skill files.
2. Use **Update for users** to publish later changes. Private edits are not distributed automatically.
3. Turn sharing off to remove only the managed recipient copies. Personal skills stay intact.

If someone already has a personal skill with that name, Oscova keeps it even when its contents differ, and that user keeps normal editing rights. Other recipients still receive the published skill. The result reports copies added or updated and personal copies preserved. Bundled skills are already **Included for all users** and have no sharing controls.

## Find skills online

1. Open **Find skills online** and search. Search becomes available as soon as one source supplies skills, and results from later sources appear without clearing your search.
2. Use **Details** to inspect a result while other sources prepare.
3. Select **Install** once your installed skills have been checked.

Each source shows its repository, branch and current preparation step, with received bytes and progress when available. Percentages describe the current step, so a new step can start at a different percentage. The overall bar counts finished sources, including failed ones, whose errors appear separately. Closing the dialog or leaving the screen cancels pending preparation. Open **Find skills online** again to retry.

The default sources use their main branches and skills folders:

| Repository | Coverage |
|---|---|
| github/awesome-copilot | Technical workflows |
| sickn33/agentic-awesome-skills | Broad workflows across many domains |
| coreyhaines31/marketingskills | Marketing |
| K-Dense-AI/scientific-agent-skills | Science and research |

Agents can search all configured sources by task or keyword without naming a repository. Agent-requested installations follow the normal approval and assignment flow. Send another message to use a newly assigned skill on the next run. A discoverable skill can still require external tools, packages or supporting files outside its folder, and both search methods keep the import and execution limits.

### Manage sources

The operator adds or removes repositories under **Manage sources**:

- Enter the owner, repository and branch. A blank branch uses main.
- **Skills folder (optional)** selects where discovery looks within the repository. Enter `skills` to search its canonical skill folders, another folder for a different layout, or leave it blank to search the whole repository.
- **Repository** opens the configured branch on GitHub.

**Search method** applies to everyone using the host after the operator selects **Save**:

| Method | What happens |
|---|---|
| **GitHub API** (default) | Discovery reads repository metadata without downloading repository files. **Details** fetches only the selected skill document. **Install** downloads only the selected skill's files. |
| **Local repository downloads** | Discovery downloads entire repositories, including scripts and files outside the selected skills folder, onto the computer running Oscova. Details and installation read the cached repository. |

Successful API metadata and local repository copies are shared by users, survive host restart and are reused for one hour. The next discovery refreshes expired data. Changing the skills-folder setting filters the existing catalog immediately, and typing a search filters loaded results without new GitHub requests. Installed skills stay separate in each user's skills folder.

- If an update fails, the dialog shows a warning and uses the last successfully cached skills. Sources without a successful cache show an error while other sources stay searchable. Close and reopen Find skills online to retry.
- In API mode, an incomplete GitHub tree response reports an error instead of silently downloading the repository.
- Offline API discovery can use previously cached metadata, but new descriptions and installation files need GitHub access.
- The operator can set the system **GitHub Skill Search** Personal Access Token for authenticated repository access. API metadata, selected-file requests and local repository downloads all use it. Tokens are not stored in repository URLs or discovery metadata. GitHub applies its own rate limits.

> **Note:**
> Switching to API mode does not remove previously downloaded repositories. Installing a selected skill can still write scripts, and discovery does not verify their safety.

## Agent-authored skills

An agent allowed to create or update skills from chat can turn a repeatable lesson, recovery step or tool-use procedure into a skill it loads later. Every request needs your approval. The prompt shows the operation, parsed skill name and description before the write happens, and the raw arguments stay available for inspection.

- Agent-authored skills are markdown-only: a complete `SKILL.md` with valid `name` and `description` frontmatter. The agent cannot create bundled scripts, resources, zip packages or registry entries through these tools.
- On approval, Oscova stores trusted provenance in the skill frontmatter, saves the skill in the installed-skills directory and assigns it to the requesting agent. The current run is not rebuilt, so the skill is available on the next run.
- Agents can update only skills with agent-authored provenance. User-imported and registry-installed skills are protected from agent updates. Editing an agent-authored skill manually keeps its update eligibility while that provenance stays in its frontmatter, so review an update request before approving replacement of your edits.

Do not approve skills that contain secrets, tokens, private personal data, one-off task notes or temporary troubleshooting logs. A skill should describe a durable procedure that is useful beyond the current conversation.

## Troubleshooting

- If Import is disabled, choose a `.md` or `.zip` file first.
- If import fails, confirm the package contains one valid `SKILL.md`.
- If import reports a conflict, uninstall the old skill or rename the incoming skill before retrying.
- If a script does not run, check the Execution environment and whether the agent has the skill assigned.
- If a local script fails immediately, confirm the matching host runtime for `.py`, `.js` or `.sh` is installed and on `PATH`.
- If a sandboxed script fails immediately, check its first line, then build or rebuild the sandbox image and review the build output.
- If online search is empty, configure skill sources or check GitHub access.
- If GitHub search is rate-limited, ask an admin to set the **GitHub Skill Search** credential.

## Related articles

- [Agents, tools and skills](references/user-guide-concepts-agents-tools-skills.md)
- [Files](references/user-guide-knowledge-and-files-files.md)
- [Main agent settings](references/user-guide-chat-and-agents-main-agent-settings.md)
