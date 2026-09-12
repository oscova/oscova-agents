using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Oscova.Agents.Skills;

/// <summary>
/// Discovers skill names and descriptions from SKILL.md files for settings-time selection.
/// </summary>
public sealed class SkillDiscoverer(ILogger? logger = null)
{
    private const string SkillFileName = "SKILL.md";
    private const int MaxSearchDepth = 2;
    private const int MaxNameLength = 64;
    private const int MaxDescriptionLength = 1024;

    private static readonly Regex ValidNameRegex = new(
        "^[a-z0-9]([a-z0-9]*-[a-z0-9])*[a-z0-9]*$",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(2));

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>
    /// Discovers available skills from the specified skills root directory.
    /// </summary>
    public IReadOnlyList<DiscoveredSkill> Discover(string skillsDirectoryPath)
    {
        if (string.IsNullOrWhiteSpace(skillsDirectoryPath) || !Directory.Exists(skillsDirectoryPath))
        {
            _logger.LogInformation("Skills directory does not exist: {Path}", skillsDirectoryPath);
            return [];
        }

        var discoveredPaths = new List<string>();
        SearchDirectoriesForSkills(skillsDirectoryPath, discoveredPaths, currentDepth: 0);

        // Sort first so duplicate-name resolution stays deterministic across platforms.
        discoveredPaths.Sort(StringComparer.OrdinalIgnoreCase);

        var discoveredSkills = new Dictionary<string, DiscoveredSkill>(StringComparer.OrdinalIgnoreCase);
        foreach (string skillDirectoryPath in discoveredPaths)
        {
            DiscoveredSkill? skill = ParseSkillFile(skillDirectoryPath);
            if (skill is null)
            {
                continue;
            }

            if (discoveredSkills.TryGetValue(skill.Name, out _))
            {
                _logger.LogWarning(
                    "Duplicate skill name '{SkillName}' discovered at '{SkillDirectoryPath}'. The first alphabetical match wins.",
                    skill.Name,
                    skillDirectoryPath);
                continue;
            }

            discoveredSkills.Add(skill.Name, skill);
        }

        return [.. discoveredSkills.Values.OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static void SearchDirectoriesForSkills(string directoryPath, List<string> results, int currentDepth)
    {
        string skillFilePath = Path.Combine(directoryPath, SkillFileName);
        if (File.Exists(skillFilePath))
        {
            results.Add(Path.GetFullPath(directoryPath));
        }

        if (currentDepth >= MaxSearchDepth)
        {
            return;
        }

        IEnumerable<string> subdirectories;
        try
        {
            subdirectories = Directory.EnumerateDirectories(directoryPath).Order(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return;
        }

        foreach (string subdirectoryPath in subdirectories)
        {
            SearchDirectoriesForSkills(subdirectoryPath, results, currentDepth + 1);
        }
    }

    private DiscoveredSkill? ParseSkillFile(string skillDirectoryPath)
    {
        string skillFilePath = Path.Combine(skillDirectoryPath, SkillFileName);
        string content;

        try
        {
            content = File.ReadAllText(skillFilePath, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read skill file '{SkillFilePath}'.", skillFilePath);
            return null;
        }

        if (!TryParseSkillDocument(content, skillFilePath, out DiscoveredSkill? skill))
        {
            return null;
        }

        return skill;
    }

    private bool TryParseSkillDocument(string content, string skillFilePath, out DiscoveredSkill? skill)
    {
        skill = null;

        if (!SkillFrontmatter.TryRead(content, out IReadOnlyDictionary<string, string> frontmatter))
        {
            _logger.LogError(
                "SKILL.md at '{SkillFilePath}' does not contain valid YAML frontmatter delimited by '---'.",
                skillFilePath);
            return false;
        }

        frontmatter.TryGetValue("name", out string? parsedName);
        frontmatter.TryGetValue("description", out string? parsedDescription);

        if (string.IsNullOrWhiteSpace(parsedName))
        {
            _logger.LogError("SKILL.md at '{SkillFilePath}' is missing a 'name' field in frontmatter.", skillFilePath);
            return false;
        }

        if (parsedName.Length > MaxNameLength || !ValidNameRegex.IsMatch(parsedName))
        {
            _logger.LogError(
                "SKILL.md at '{SkillFilePath}' has an invalid 'name' value. It must be {MaxNameLength} characters or fewer and use lowercase letters, numbers, and hyphens only.",
                skillFilePath,
                MaxNameLength);
            return false;
        }

        string directoryName = Path.GetFileName(Path.GetDirectoryName(skillFilePath)) ?? string.Empty;
        if (!string.Equals(parsedName, directoryName, StringComparison.Ordinal))
        {
            _logger.LogError(
                "SKILL.md at '{SkillFilePath}' has skill name '{SkillName}' but the parent directory is '{DirectoryName}'.",
                skillFilePath,
                parsedName,
                directoryName);
            return false;
        }

        if (string.IsNullOrWhiteSpace(parsedDescription))
        {
            _logger.LogError("SKILL.md at '{SkillFilePath}' is missing a 'description' field in frontmatter.", skillFilePath);
            return false;
        }

        if (parsedDescription.Length > MaxDescriptionLength)
        {
            _logger.LogError(
                "SKILL.md at '{SkillFilePath}' has an invalid 'description' value. It must be {MaxDescriptionLength} characters or fewer.",
                skillFilePath,
                MaxDescriptionLength);
            return false;
        }

        skill = new DiscoveredSkill
        {
            Name = parsedName,
            Description = parsedDescription
        };

        return true;
    }
}
