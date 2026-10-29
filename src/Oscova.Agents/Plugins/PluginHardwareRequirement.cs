using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Oscova.Agents.Plugins;

/// <summary>Declares package capability without claiming runtime availability.</summary>
public sealed record PluginHardwareRequirement(string Backend, int? CudaMajor = null)
{
    /// <summary>Identifies explicit hardware metadata independently of whether a declaration is understood.</summary>
    public const string TagPrefix = "oscova-hardware.";

    /// <summary>Checks the recognized backend and its optional CUDA version.</summary>
    public bool IsKnown => Backend is "cpu" or "metal" or "managed" ? CudaMajor is null
        : Backend == "cuda" && (CudaMajor is null || CudaMajor > 0);

    /// <summary>Reads one RID's tags. Missing, malformed or conflicting declarations remain unknown.</summary>
    public static PluginHardwareRequirement? FromTags(IEnumerable<string> tags, string runtimeIdentifier)
    {
        var prefix = TagPrefix + runtimeIdentifier + ".";
        var declarations = tags.Where(tag => tag.StartsWith(prefix, StringComparison.Ordinal))
            .Select(tag => tag[prefix.Length..]).Distinct(StringComparer.Ordinal).ToArray();
        if (declarations.Length == 0) { return null; }

        var backends = declarations.Where(tag => tag is "cpu" or "cuda" or "metal" or "managed").ToArray();
        if (backends.Length != 1) { return null; }

        int? cudaMajor = null;
        foreach (var declaration in declarations)
        {
            if (declaration == backends[0]) { continue; }
            if (backends[0] != "cuda" || !declaration.StartsWith("cuda", StringComparison.Ordinal)
                || !int.TryParse(declaration[4..], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
                || major <= 0 || cudaMajor is not null) { return null; }

            cudaMajor = major;
        }

        return new(backends[0], cudaMajor);
    }
}
