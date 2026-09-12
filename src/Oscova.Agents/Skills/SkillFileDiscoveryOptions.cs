using System;
using System.Collections.Generic;
using Microsoft.Agents.AI;
using Oscova.Agents.Utils;

#pragma warning disable MAAI001

namespace Oscova.Agents.Skills;

/// <summary>Provides canonical file-skill discovery rules shared by runtime discovery and import validation.</summary>
public static class SkillFileDiscoveryOptions
{
    /// <summary>Gets the supported resource file extensions.</summary>
    public static IReadOnlyList<string> ResourceExtensions { get; } =
        Array.AsReadOnly<string>([".md", ".json", ".yaml", ".yml", ".csv", ".xml", ".txt"]);

    /// <summary>Gets the top-level directories used for executable scripts.</summary>
    public static IReadOnlyList<string> ScriptDirectories { get; } = Array.AsReadOnly<string>(["scripts"]);

    /// <summary>Gets the top-level directories used for skill resources.</summary>
    public static IReadOnlyList<string> ResourceDirectories { get; } = Array.AsReadOnly<string>(["references", "assets"]);

    /// <summary>Creates file-skill discovery options with scripts enabled or disabled.</summary>
    public static AgentFileSkillsSourceOptions Create(bool scriptsEnabled)
    {
        return new AgentFileSkillsSourceOptions
        {
            AllowedScriptExtensions = scriptsEnabled ? null : [],
            AllowedResourceExtensions = ResourceExtensions,
            SearchDepth = 2,
            ScriptFilter = context => PathHelper.IsDirectChildOfAnyDirectory(context.RelativeFilePath, ScriptDirectories),
            ResourceFilter = context => PathHelper.IsDirectChildOfAnyDirectory(context.RelativeFilePath, ResourceDirectories)
        };
    }
}

#pragma warning restore MAAI001
