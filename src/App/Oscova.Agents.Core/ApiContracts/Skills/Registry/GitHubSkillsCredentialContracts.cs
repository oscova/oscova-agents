namespace Oscova.Agents.Core.ApiContracts.Skills.Registry;

/// <summary>
/// Reports whether the system-scoped GitHub Personal Access Token used by the skill registry is set.
/// </summary>
public sealed class GitHubSkillsCredentialStatusContract
{
    public bool IsConfigured { get; set; }
}

/// <summary>
/// Stores a new system-scoped GitHub Personal Access Token used by the skill registry.
/// </summary>
public sealed class SaveGitHubSkillsCredentialContract
{
    public string? Token { get; set; }
}
