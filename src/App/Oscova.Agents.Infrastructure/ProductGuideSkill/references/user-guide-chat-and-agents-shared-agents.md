# Shared agents

Shared agents let admins and moderators make one configured agent available to other users while each user keeps private personal context.

## Share an agent

1. Open the custom agent's editor as an admin or moderator.
2. Turn on **Share with all users**.
3. Leave the allowed-user selection empty to share with everyone, or select specific users to restrict it.

- Admins and moderators can edit the shared definition. Other users see **Customize** instead of **Edit**, which opens only the personal context editor. It cannot change tools, instructions, model providers or file access.
- Sharing controls visibility, not ownership of every setting. A shared agent runs with the saved shared definition, and users who cannot manage shared agents cannot delete it or change its runtime surface.

## Personal context

Personal context is text stored for one user and one shared agent. Use it for preferred names, output style, team names, recurring repositories, responsibilities or other stable context.

- The shared agent's system instruction must include `{{ user_context }}`. Without that variable, the context is saved but not injected.
- Personal context is per user. It is not a global edit to the shared agent.
- Select **Save** in the Customize view before returning to the agent list. Changes are not saved automatically.
- Do not put secrets, passwords or API keys in personal context. Use Credentials settings for secrets.
- If an administrator removes a user from the allowed list, that user's saved personal context for the agent is removed.

## Permissions and tools

Sharing an agent does not grant admin rights, but it makes that configured assistant available to the selected audience. Review tools, workspaces, model provider, memory, skills and exposed agent tools before sharing. The called agent keeps the permissions saved on the shared definition.

> **Caution:**
> If a shared agent has write-capable tools or writable directories, allowed users can ask it to change files or services within its configured policies. Review shared host-folder grants particularly carefully.

- Sharing the definition does not copy the owner's credentials or connected accounts.
- Provider lookup uses the running user's available catalog, which includes the operator's shared providers and any permitted personal providers. Shared provider calls use the provider owner's credentials and budget.
- If a saved provider override is unavailable, the agent falls back to the running user's default provider.
- Connected-service tools use the running user's own connected accounts.

## When to share

Shared agents work well for standardized assistants such as support triage, release notes, operations summaries or internal knowledge helpers. Keep experimental or personal agents private until their instructions and tool access are stable.

Use user restrictions when the shared agent contains team-specific knowledge or workspace access. Keep credentials in the credential store and grant the narrowest tool access that works.

## Related articles

- [Custom agents](references/user-guide-chat-and-agents-custom-agents.md)
- [Main agent settings](references/user-guide-chat-and-agents-main-agent-settings.md)
- [Tool governance](references/user-guide-concepts-tool-governance.md)
