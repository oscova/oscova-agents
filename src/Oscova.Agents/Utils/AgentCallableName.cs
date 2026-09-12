using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Oscova.Agents.Utils;

/// <summary>Normalizes agent display names to the function names used for agent-as-tool calls.</summary>
public static partial class AgentCallableName
{
    /// <summary>Returns the callable name produced for an agent display name.</summary>
    public static string Normalize(string name)
    {
        return InvalidNameCharacters().Replace(name, "_");
    }

    /// <summary>Returns the base name, or the first "Name N" variant whose callable name is free.</summary>
    public static string MakeUniqueDisplayName(string baseName, IEnumerable<string> existingNames)
    {
        var existingCallableNames = existingNames
            .Select(Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = baseName;
        var suffix = 2;
        while (existingCallableNames.Contains(Normalize(name)))
        {
            name = $"{baseName} {suffix}";
            suffix++;
        }

        return name;
    }

    [GeneratedRegex("[^0-9A-Za-z]+", RegexOptions.None, 1000)]
    private static partial Regex InvalidNameCharacters();
}
