namespace Oscova.Agents.Core.Skills.Registry;

/// <summary>Chooses whether discovery reads GitHub metadata or downloads complete repositories.</summary>
public enum SkillRegistrySearchMethod
{
    GitHubApi,
    LocalRepository
}
