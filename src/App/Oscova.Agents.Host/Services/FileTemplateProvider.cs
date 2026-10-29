namespace Oscova.Agents.Host.Services;

/// <summary>
/// Resolves default template content for well-known file names.
/// Used during file creation to pre-populate files with a useful starting structure.
/// </summary>
public static class FileTemplateProvider
{
    /// <summary>
    /// Returns the default template content for the given file name,
    /// or <see langword="null"/> if no template is defined for that name.
    /// Matching is case-sensitive.
    /// </summary>
    /// <param name="fileName">The file name to look up (e.g. <c>SKILL.md</c>).</param>
    public static string? GetTemplate(string fileName) => fileName switch
    {
        "SKILL.md" => SkillTemplate,
        _ => null
    };

    private const string SkillTemplate =
        """
        ---
        name: skill-name
        description: Describe what this skill does and when to use it.
        ---

        ## Usage

        Describe the steps to follow when this skill is invoked.
        1. Step one
        2. Step two
        """;
}
