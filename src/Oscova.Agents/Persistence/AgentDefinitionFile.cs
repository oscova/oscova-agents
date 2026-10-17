using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace Oscova.Agents.Persistence;

/// <summary>
/// YAML frontmatter shape for an agent definition stored in an agent.md file.
/// </summary>
public sealed class AgentDefinitionFile
{
    /// <summary>Identifies the agent definition shape understood by this release.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets or sets the persisted definition shape identifier.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    /// <summary>
    /// Gets or sets the persisted agent identifier.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the persisted display name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the opaque source of a managed instruction.</summary>
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
    public string? InstructionSource { get; set; }

    /// <summary>Gets or sets the saved instruction composition mode.</summary>
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
    public string? InstructionMode { get; set; }

    /// <summary>Gets or sets the retained default supplied by an agent contribution.</summary>
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
    public string? DefaultInstruction { get; set; }

    /// <summary>
    /// Gets or sets the persisted agent description.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether this agent is the user's main agent.
    /// </summary>
    public bool IsMain { get; set; }

    /// <summary>
    /// Gets or sets whether this agent can be visible outside its owner.
    /// </summary>
    public bool IsShared { get; set; }

    /// <summary>
    /// Gets or sets whether conversation compaction is enabled.
    /// </summary>
    public bool IsCompactionEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets whether memory context is enabled.
    /// </summary>
    public bool IsMemoryEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the agent gets an in-session todo list.
    /// </summary>
    public bool IsTodoEnabled { get; set; }

    /// <summary>
    /// Gets or sets whether the agent may use only loopback and local-network endpoints.
    /// </summary>
    public bool IsLocalOnly { get; set; }


    /// <summary>
    /// Gets or sets whether this agent may be exposed as a tool.
    /// </summary>
    public bool IsExposedAsTool { get; set; }

    /// <summary>
    /// Gets or sets the optional chat model provider override for this agent.
    /// </summary>
    public string? ModelOverrideId { get; set; }

    /// <summary>Gets or sets whether consultations may use the caller-selected model provider.</summary>
    public bool UseCallerModelForConsultations { get; set; }

    /// <summary>
    /// Gets or sets the persisted owner user identifier.
    /// </summary>
    public string? OwnerUserId { get; set; }

    /// <summary>
    /// Gets or sets qualified tool names allowed for the agent.
    /// </summary>
    public IList<string> AllowedToolNames { get; set; } = [];

    /// <summary>Gets or sets whether this agent's tools are hidden behind the search_tools discovery tool.</summary>
    public bool IsToolSearchEnabled { get; set; }

    /// <summary>Gets or sets the qualified tool names kept always-visible when tool search is enabled.</summary>
    public IList<string> PinnedToolNames { get; set; } = [];

    /// <summary>
    /// Gets or sets exposed agent identifiers allowed as tools.
    /// </summary>
    public IList<string> AgentToolIds { get; set; } = [];

    /// <summary>
    /// Gets or sets skill names allowed for the agent.
    /// </summary>
    public IList<string> AllowedSkillNames { get; set; } = [];

    /// <summary>
    /// Gets or sets legacy workspace identifiers allowed for the agent.
    /// </summary>
    public IList<string> AllowedWorkspaces { get; set; } = [];

    /// <summary>
    /// Gets or sets user identifiers allowed to access a shared agent.
    /// </summary>
    public IList<string> AllowedUserIds { get; set; } = [];

    /// <summary>
    /// Gets or sets explicit directory access entries allowed for the agent.
    /// </summary>
    public IList<AllowedDirectoryFile> AllowedDirectories { get; set; } = [];
}

/// <summary>
/// YAML shape for one directory an agent may access through workspace-aware tools.
/// </summary>
public sealed class AllowedDirectoryFile
{
    /// <summary>
    /// Gets or sets the persisted directory path or provider value.
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the user-facing directory label.
    /// </summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the serialized directory source kind.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the serialized directory access mode.
    /// </summary>
    public string AccessMode { get; set; } = string.Empty;
}
