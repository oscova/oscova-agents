namespace Oscova.Agents.Core.Skills.Registry;

/// <summary>Identifies a configured GitHub repository and its discovery folder.</summary>
public sealed class SkillRegistrySource
{
    public string Owner { get; set; } = string.Empty;
    public string Repo { get; set; } = string.Empty;
    public string Branch { get; set; } = "main";

    /// <summary>Limits discovered skills to this repository folder, or all folders when empty.</summary>
    public string SkillRoot { get; set; } = string.Empty;

    public string DisplayName => $"{Owner}/{Repo}";
}
