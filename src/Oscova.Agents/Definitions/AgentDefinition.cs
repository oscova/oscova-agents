using Oscova.Agents.Policies;
using Oscova.Agents.Workspace;
using System;
using System.Collections.Generic;

namespace Oscova.Agents.Definitions;

/// <summary>
/// Defines an agent that can be built by the Oscova agent runtime.
/// </summary>
public class AgentDefinition
{
    /// <summary>
    /// Gets or sets the stable identifier used to persist and cache the agent.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Gets or sets the display name shown to users and the model runtime.
    /// </summary>
    public string Name { get; set; } = "Assistant";

    /// <summary>
    /// Gets or sets a short description of the agent's purpose.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the system instruction used to shape the agent's behavior.
    /// </summary>
    public string SystemInstruction { get; set; } = "You are a helpful assistant.";

    /// <summary>
    /// Gets or sets the optional chat model provider override for this agent.
    /// </summary>
    public string? ModelOverrideId { get; set; }

    /// <summary>
    /// Gets or sets the qualified tool names this agent may use.
    /// </summary>
    public IList<string> AllowedToolNames { get; set; } = [];

    /// <summary>
    /// Sparse governance overrides for selected tools. Missing tools inherit defaults.
    /// </summary>
    public IList<AgentToolPolicy> ToolPolicies { get; set; } = [];

    /// <summary>Gets or sets whether this agent's tools are hidden behind the search_tools discovery tool.</summary>
    public bool IsToolSearchEnabled { get; set; }

    /// <summary>Gets or sets the qualified tool names kept always-visible when tool search is enabled.</summary>
    public IList<string> PinnedToolNames { get; set; } = [];

    /// <summary>
    /// Gets or sets whether long conversations should be summarized when they approach the token budget.
    /// </summary>
    public bool IsCompactionEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the agent receives long-term memory context.
    /// </summary>
    public bool IsMemoryEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the agent gets an in-session todo list.
    /// </summary>
    public bool IsTodoEnabled { get; set; }

    /// <summary>
    /// Gets or sets whether this agent may use only loopback and local-network endpoints.
    /// </summary>
    public bool IsLocalOnly { get; set; }

    /// <summary>
    /// Gets or sets whether this agent stores its chats and task runs encrypted at rest.
    /// </summary>
    public bool IsConfidential { get; set; }

    /// <summary>
    /// Gets or sets whether other agents may call this agent as a tool.
    /// </summary>
    public bool IsExposedAsTool { get; set; }

    /// <summary>
    /// Gets or sets the exposed agent identifiers this agent may call as tools.
    /// </summary>
    public IList<string> AgentToolIds { get; set; } = [];

    /// <summary>
    /// Gets or sets the skill names this agent may load.
    /// </summary>
    public IList<string> AllowedSkillNames { get; set; } = [];

    /// <summary>
    /// Gets or sets legacy workspace identifiers this agent may access.
    /// </summary>
    public IList<string> AllowedWorkspaces { get; set; } = [];

    /// <summary>
    /// Gets or sets the explicit filesystem directories this agent may read or write.
    /// </summary>
    public IList<AllowedDirectory> AllowedDirectories { get; set; } = [];

    /// <summary>
    /// Gets or sets whether the definition is visible beyond its owner.
    /// </summary>
    public bool IsShared { get; set; }

    /// <summary>
    /// Gets or sets whether this definition is the user's main agent.
    /// </summary>
    public bool IsMain { get; set; }

    /// <summary>
    /// Gets or sets the user that owns the definition, when ownership is persisted with the model.
    /// </summary>
    public string? OwnerUserId { get; set; }

    /// <summary>
    /// Gets or sets the users allowed to see or run a shared agent.
    /// </summary>
    public IList<string> AllowedUserIds { get; set; } = [];
}
