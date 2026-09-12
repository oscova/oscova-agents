using System.Text;
using Oscova.Agents.Skills;

namespace Oscova.Agents.Tests.Framework.Skills;

public sealed class SkillDiscovererTests : IDisposable
{
    private readonly string _testRoot;
    private readonly SkillDiscoverer _discoverer = new();

    public SkillDiscovererTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "SkillDiscovererTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    [Fact]
    public void Discover_ValidSkill_ReturnsNameAndDescription()
    {
        string skillDirectory = CreateSkillDirectory("my-skill");
        WriteSkillFile(skillDirectory, """
            ---
            name: my-skill
            description: A test skill for unit testing
            ---
            # Skill Body
            """);

        IReadOnlyList<DiscoveredSkill> skills = _discoverer.Discover(_testRoot);

        DiscoveredSkill skill = Assert.Single(skills);
        Assert.Equal("my-skill", skill.Name);
        Assert.Equal("A test skill for unit testing", skill.Description);
    }

    [Fact]
    public void Discover_QuotedValues_ParsesCorrectly()
    {
        string skillDirectory = CreateSkillDirectory("quoted-skill");
        WriteSkillFile(skillDirectory, """
            ---
            name: quoted-skill
            description: "A skill with a quoted description"
            ---
            Body content.
            """);

        IReadOnlyList<DiscoveredSkill> skills = _discoverer.Discover(_testRoot);

        DiscoveredSkill skill = Assert.Single(skills);
        Assert.Equal("A skill with a quoted description", skill.Description);
    }

    [Fact]
    public void Discover_MultilineDescription_ParsesYamlBlockScalar()
    {
        string skillDirectory = CreateSkillDirectory("checklist-helper");
        WriteSkillFile(skillDirectory, """
            ---
            name: checklist-helper
            description: |
              Helps prepare a project checklist. Use this skill when:
              - Gathering validation items
              - Summarizing completion notes
            ---
            Body content.
            """);

        IReadOnlyList<DiscoveredSkill> skills = _discoverer.Discover(_testRoot);

        DiscoveredSkill skill = Assert.Single(skills);
        Assert.StartsWith("Helps prepare a project checklist.", skill.Description);
        Assert.Contains("- Summarizing completion notes", skill.Description);
        Assert.NotEqual("|", skill.Description);
    }

    [Fact]
    public void Discover_DuplicateSkillNames_KeepsAlphabeticallyFirstDirectory()
    {
        string firstParent = Path.Combine(_testRoot, "sub1");
        string secondParent = Path.Combine(_testRoot, "sub2");
        Directory.CreateDirectory(firstParent);
        Directory.CreateDirectory(secondParent);

        string firstSkillDirectory = Path.Combine(firstParent, "dup-skill");
        string secondSkillDirectory = Path.Combine(secondParent, "dup-skill");
        Directory.CreateDirectory(firstSkillDirectory);
        Directory.CreateDirectory(secondSkillDirectory);

        WriteSkillFile(firstSkillDirectory, """
            ---
            name: dup-skill
            description: First instance
            ---
            Body.
            """);

        WriteSkillFile(secondSkillDirectory, """
            ---
            name: dup-skill
            description: Second instance
            ---
            Body.
            """);

        IReadOnlyList<DiscoveredSkill> skills = _discoverer.Discover(_testRoot);

        DiscoveredSkill skill = Assert.Single(skills);
        Assert.Equal("First instance", skill.Description);
    }

    [Fact]
    public void Discover_MissingDescription_SkipsSkill()
    {
        string skillDirectory = CreateSkillDirectory("no-desc");
        WriteSkillFile(skillDirectory, """
            ---
            name: no-desc
            ---
            Body.
            """);

        IReadOnlyList<DiscoveredSkill> skills = _discoverer.Discover(_testRoot);

        Assert.Empty(skills);
    }

    [Fact]
    public void Discover_MissingName_SkipsSkill()
    {
        string skillDirectory = CreateSkillDirectory("no-name");
        WriteSkillFile(skillDirectory, """
            ---
            description: Missing name field
            ---
            Body.
            """);

        IReadOnlyList<DiscoveredSkill> skills = _discoverer.Discover(_testRoot);

        Assert.Empty(skills);
    }

    [Fact]
    public void Discover_InvalidNameFormat_SkipsSkill()
    {
        string skillDirectory = CreateSkillDirectory("BadName");
        WriteSkillFile(skillDirectory, """
            ---
            name: BadName
            description: Invalid name format
            ---
            Body.
            """);

        IReadOnlyList<DiscoveredSkill> skills = _discoverer.Discover(_testRoot);

        Assert.Empty(skills);
    }

    [Fact]
    public void Discover_NameTooLong_SkipsSkill()
    {
        string longName = new string('a', 65);
        string skillDirectory = CreateSkillDirectory(longName);
        WriteSkillFile(skillDirectory, $"""
            ---
            name: {longName}
            description: Name is too long
            ---
            Body.
            """);

        IReadOnlyList<DiscoveredSkill> skills = _discoverer.Discover(_testRoot);

        Assert.Empty(skills);
    }

    [Fact]
    public void Discover_NameMismatchWithDirectory_SkipsSkill()
    {
        string skillDirectory = CreateSkillDirectory("actual-dir");
        WriteSkillFile(skillDirectory, """
            ---
            name: different-name
            description: Name does not match directory
            ---
            Body.
            """);

        IReadOnlyList<DiscoveredSkill> skills = _discoverer.Discover(_testRoot);

        Assert.Empty(skills);
    }

    [Fact]
    public void Discover_DescriptionTooLong_SkipsSkill()
    {
        string skillDirectory = CreateSkillDirectory("long-description");
        string longDescription = new string('d', 1025);
        WriteSkillFile(skillDirectory, $"""
            ---
            name: long-description
            description: {longDescription}
            ---
            Body.
            """);

        IReadOnlyList<DiscoveredSkill> skills = _discoverer.Discover(_testRoot);

        Assert.Empty(skills);
    }

    [Fact]
    public void Discover_SearchesTwoLevelsDeep()
    {
        string skillDirectory = Path.Combine(_testRoot, "group", "alpha");
        Directory.CreateDirectory(skillDirectory);
        WriteSkillFile(skillDirectory, """
            ---
            name: alpha
            description: Nested skill
            ---
            Body.
            """);

        IReadOnlyList<DiscoveredSkill> skills = _discoverer.Discover(_testRoot);

        DiscoveredSkill skill = Assert.Single(skills);
        Assert.Equal("alpha", skill.Name);
    }

    [Fact]
    public void Discover_Utf8Bom_ParsesCorrectly()
    {
        string skillDirectory = CreateSkillDirectory("bom-skill");
        string content = "\uFEFF---\nname: bom-skill\ndescription: Skill with BOM\n---\nBody content.";
        File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), content, new UTF8Encoding(true));

        IReadOnlyList<DiscoveredSkill> skills = _discoverer.Discover(_testRoot);

        DiscoveredSkill skill = Assert.Single(skills);
        Assert.Equal("bom-skill", skill.Name);
    }

    [Fact]
    public void SharedFilePolicy_WhenCallerAttemptsMutation_PreservesLaterDiscoveryRules()
    {
        AssertPolicyIsImmutable(SkillFileDiscoveryOptions.ResourceExtensions);
        AssertPolicyIsImmutable(SkillFileDiscoveryOptions.ScriptDirectories);
        AssertPolicyIsImmutable(SkillFileDiscoveryOptions.ResourceDirectories);

        var options = SkillFileDiscoveryOptions.Create(scriptsEnabled: true);
        Assert.Contains(".md", options.AllowedResourceExtensions!);
    }

    private string CreateSkillDirectory(string name)
    {
        string path = Path.Combine(_testRoot, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteSkillFile(string skillDirectory, string content)
    {
        File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), content);
    }

    private static void AssertPolicyIsImmutable(IReadOnlyList<string> policy)
    {
        var mutablePolicy = Assert.IsAssignableFrom<IList<string>>(policy);
        string original = mutablePolicy[0];

        try
        {
            Assert.Throws<NotSupportedException>(() => mutablePolicy[0] = "mutated");
        }
        finally
        {
            if (!string.Equals(mutablePolicy[0], original, StringComparison.Ordinal))
            {
                mutablePolicy[0] = original;
            }
        }
    }
}
