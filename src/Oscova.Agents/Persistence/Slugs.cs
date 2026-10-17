using System.Collections.Generic;
using System.Text;

namespace Oscova.Agents.Persistence;

/// <summary>
/// Produces lowercase ASCII slugs. Each method applies a rule set tailored to a specific use case;
/// pick the method that matches the target context rather than calling a single "generic" slug routine.
/// </summary>
public static class Slugs
{
    private const int AgentFolderMaxLength = 60;
    private const string AgentFolderFallback = "agent";
    private const int TaskTriggerMaxLength = 80;
    private const string TaskTriggerFallback = "task";

    /// <summary>
    /// Builds a slug for an on-disk agent folder name: lowercase ASCII, capped at 60 characters with
    /// word-boundary truncation, and falls back to <c>"agent"</c> when no usable characters remain.
    /// </summary>
    public static string ForAgentFolder(string name)
    {
        var builder = new StringBuilder();
        var previousWasSeparator = false;

        foreach (char character in name.ToLowerInvariant())
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(character);
                previousWasSeparator = false;
                continue;
            }

            if (!previousWasSeparator && builder.Length > 0)
            {
                builder.Append('-');
                previousWasSeparator = true;
            }
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length > AgentFolderMaxLength)
        {
            slug = slug[..AgentFolderMaxLength].TrimEnd('-');
            var lastSeparator = slug.LastIndexOf('-');
            if (lastSeparator > 0)
            {
                slug = slug[..lastSeparator];
            }
        }

        return string.IsNullOrWhiteSpace(slug) ? AgentFolderFallback : slug;
    }

    /// <summary>
    /// Builds a slug for a free-form credential alias: lowercase ASCII with no length cap.
    /// Returns an empty string when no usable characters remain so callers can reject the input.
    /// </summary>
    public static string ForCredentialAlias(string alias)
    {
        var builder = new StringBuilder(alias.Length);
        var lastWasHyphen = false;
        foreach (var character in alias.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                lastWasHyphen = false;
            }
            else if (!lastWasHyphen && builder.Length > 0)
            {
                builder.Append('-');
                lastWasHyphen = true;
            }
        }

        return builder.ToString().Trim('-');
    }

    /// <summary>
    /// Builds a URL-safe slug for externally triggered task endpoints and falls back to <c>"task"</c>.
    /// </summary>
    public static string ForTaskTrigger(string name)
    {
        var builder = new StringBuilder();
        var previousWasSeparator = false;

        foreach (char character in name.Trim().ToLowerInvariant())
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(character);
                previousWasSeparator = false;
                continue;
            }

            if (!previousWasSeparator && builder.Length > 0)
            {
                builder.Append('-');
                previousWasSeparator = true;
            }
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length > TaskTriggerMaxLength)
        {
            slug = slug[..TaskTriggerMaxLength].TrimEnd('-');
        }

        return string.IsNullOrWhiteSpace(slug) ? TaskTriggerFallback : slug;
    }

    /// <summary>
    /// Returns <paramref name="slug"/> if free, otherwise appends a numeric suffix until an unused value is found.
    /// </summary>
    public static string MakeUnique(string slug, ISet<string> existingSlugs)
    {
        if (!existingSlugs.Contains(slug))
        {
            return slug;
        }

        var suffix = 2;
        while (true)
        {
            var candidate = $"{slug}-{suffix}";
            if (!existingSlugs.Contains(candidate))
            {
                return candidate;
            }

            suffix++;
        }
    }
    /// <summary>Qualifies a task trigger name with its owner while retaining the existing URL normalization.</summary>
    public static string ForUserTaskTrigger(string ownerUserId, string name)
    {
        var prefix = ForTaskTrigger(ownerUserId) + "-";
        var trimmed = name.Trim();
        var chosenName = trimmed.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase)
            ? trimmed[prefix.Length..]
            : trimmed;
        return ForTaskTrigger(prefix + ForTaskTrigger(chosenName));
    }
}