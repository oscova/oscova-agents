namespace Oscova.Agents.Core.ApiContracts.Skills.Registry;

/// <summary>
/// Targets one remote skill in a GitHub repository for installation into the calling user's skill folder.
/// </summary>
public sealed class InstallRemoteSkillRequest
{
    public string Owner { get; set; } = string.Empty;
    public string Repo { get; set; } = string.Empty;
    public string Branch { get; set; } = "main";
    public string SkillPath { get; set; } = string.Empty;
}
