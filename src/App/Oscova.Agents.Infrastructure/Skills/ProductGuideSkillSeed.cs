using Oscova.Agents.Infrastructure.Utils;

namespace Oscova.Agents.Infrastructure.Skills;

/// <summary>Installs the bundled skills supplied to every account.</summary>
public static class ProductGuideSkillSeed
{
    public const string SkillName = "oscova-agents-guide";
    public const string VisualizeSkillName = "visualize";
    /// <summary>Tells the receiving agent how to answer a <c>/research</c> message with a saved report.</summary>
    public const string ResearchSkillName = "oscova-research";
    public static readonly IReadOnlySet<string> BundledSkillNames = new HashSet<string>(StringComparer.Ordinal)
    {
        SkillName, VisualizeSkillName, ResearchSkillName
    };
    private const string ResourcePrefix = "Oscova.Agents.Infrastructure.ProductGuideSkill.";

    internal static void Refresh(string skillsDirectory)
    {
        InstallSingleFile(skillsDirectory, VisualizeSkillName);
        InstallSingleFile(skillsDirectory, ResearchSkillName);
        Install(skillsDirectory, SkillName, ReadEmbeddedFiles());
    }

    private static void InstallSingleFile(string skillsDirectory, string skillName)
    {
        using var stream = typeof(ProductGuideSkillSeed).Assembly.GetManifestResourceStream(
            $"Oscova.Agents.Infrastructure.SeedContent.Skills.{skillName}.md")
            ?? throw new InvalidDataException($"The bundled {skillName} skill is unavailable.");
        using var reader = new StreamReader(stream);
        Install(skillsDirectory, skillName, new Dictionary<string, byte[]>
        {
            ["SKILL.md"] = System.Text.Encoding.UTF8.GetBytes(reader.ReadToEnd())
        });
    }

    private static void Install(string skillsDirectory, string skillName, IReadOnlyDictionary<string, byte[]> expected)
    {
        var destination = Path.Combine(skillsDirectory, skillName);
        if (Matches(destination, expected))
        {
            return;
        }

        Directory.CreateDirectory(skillsDirectory);
        var staging = $"{destination}.staging-{Guid.NewGuid():N}";
        var backup = $"{destination}.backup-{Guid.NewGuid():N}";
        var installed = false;
        try
        {
            foreach (var (relativePath, bytes) in expected)
            {
                var path = Path.Combine(staging, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes);
            }

            if (Directory.Exists(destination))
            {
                DirectoryUtil.MoveWithRetry(destination, backup);
            }

            try
            {
                DirectoryUtil.MoveWithRetry(staging, destination);
                installed = true;
            }
            catch
            {
                if (Directory.Exists(backup) && !Directory.Exists(destination))
                {
                    DirectoryUtil.MoveWithRetry(backup, destination);
                }

                throw;
            }

            if (Directory.Exists(backup))
            {
                Directory.Delete(backup, recursive: true);
            }
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            if (installed && Directory.Exists(backup))
            {
                Directory.Delete(backup, recursive: true);
            }
        }
    }

    private static IReadOnlyDictionary<string, byte[]> ReadEmbeddedFiles()
    {
        var assembly = typeof(ProductGuideSkillSeed).Assembly;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            var suffix = resourceName[ResourcePrefix.Length..];
            var relativePath = suffix == "SKILL.md"
                ? "SKILL.md"
                : suffix.StartsWith("references.", StringComparison.Ordinal)
                    ? Path.Combine("references", suffix["references.".Length..])
                    : throw new InvalidDataException($"Unexpected product-guide resource: {resourceName}");
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidDataException($"Cannot read product-guide resource: {resourceName}");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            if (!files.TryAdd(relativePath, buffer.ToArray()))
            {
                throw new InvalidDataException($"Duplicate product-guide resource path: {relativePath}");
            }
        }

        if (!files.ContainsKey("SKILL.md") || !files.Keys.Any(path => path.StartsWith($"references{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            throw new InvalidDataException("The embedded product guide is incomplete.");
        }

        return files;
    }

    private static bool Matches(string destination, IReadOnlyDictionary<string, byte[]> expected)
    {
        if (!Directory.Exists(destination))
        {
            return false;
        }

        var actualPaths = Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(destination, path))
            .ToHashSet(StringComparer.Ordinal);
        if (!actualPaths.SetEquals(expected.Keys))
        {
            return false;
        }

        return expected.All(file =>
            File.ReadAllBytes(Path.Combine(destination, file.Key)).AsSpan().SequenceEqual(file.Value));
    }
}
