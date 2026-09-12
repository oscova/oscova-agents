using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Oscova.Agents.Skills;

/// <summary>
/// Parses the YAML frontmatter block at the top of a SKILL.md file into a flat scalar key/value map.
/// </summary>
public static class SkillFrontmatter
{
    private static readonly Regex FrontmatterRegex = new(
        @"\A\uFEFF?---\s*$(?<yaml>.+?)^---\s*$",
        // Multiline anchors the '---' delimiters to line boundaries; Singleline lets the body match span newlines.
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// Reads flat top-level scalar keys from the frontmatter block. Keys are case-insensitive.
    /// Returns false if no frontmatter delimited by '---' is present or its YAML is invalid.
    /// </summary>
    public static bool TryRead(string content, out IReadOnlyDictionary<string, string> values)
    {
        Match frontmatterMatch = FrontmatterRegex.Match(content);
        if (!frontmatterMatch.Success)
        {
            values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return false;
        }

        string yamlContent = frontmatterMatch.Groups["yaml"].Value.Trim();
        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var yaml = new YamlStream();
            using var reader = new StringReader(yamlContent);
            yaml.Load(reader);

            if (yaml.Documents.Count > 0 && yaml.Documents[0].RootNode is YamlMappingNode mapping)
            {
                foreach (var item in mapping.Children)
                {
                    if (item.Key is not YamlScalarNode { Value: not null } key)
                    {
                        continue;
                    }

                    if (item.Value is YamlScalarNode { Value: not null } value)
                    {
                        parsed[key.Value] = value.Value;
                    }
                    else
                    {
                        parsed[key.Value] = string.Empty;
                    }
                }
            }
        }
        catch (YamlException)
        {
            values = parsed;
            return false;
        }

        values = parsed;
        return true;
    }
}
