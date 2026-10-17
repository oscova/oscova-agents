using Oscova.Agents.Toolsets;
using NuGet.Packaging;
using Oscova.Agents.Persistence;
using Oscova.Agents.Tests.TestSupport.TestPlugin;
using Oscova.Agents.Tests.TestSupport;

namespace Oscova.Agents.Plugins.Tests.AgentTemplates;

/// <summary>Proves shipped starter content and package ownership through the real loader.</summary>
public sealed class AgentTemplatesPluginTests
{
    [Fact]
    public void LoadDirectory_ContributesEightEmbeddedTemplates()
    {
        using var root = TestPluginRoot.Create();
        root.AddPluginFromProjectOutput("src/Oscova.Agents.Plugins.AgentTemplates", "oscova.agent-templates");
        var catalog = AgentPluginLoader.LoadDirectory(root.RootPath);
        Assert.Empty(catalog.Diagnostics);
        var plugin = Assert.Single(catalog.Plugins);
        Assert.Equal(new[]
        {
            "researcher", "study-coach", "interview-coach", "writing-brand-editor",
            "document-analyst", "meeting-analyst", "planning-assistant", "email-assistant"
        }, plugin.AgentTemplates.Select(template => template.Id));
        Assert.Empty(plugin.Toolsets);
        Assert.Empty(plugin.Channels);
        Assert.Empty(plugin.OAuthProviders);
        string[] common =
        [
            ToolNaming.BuildToolsetQualifiedName<CodingToolset>(nameof(CodingToolset.ReadFile)),
            ToolNaming.BuildToolsetQualifiedName<PdfToolset>(nameof(PdfToolset.ExtractPdfText)),
            ToolNaming.BuildToolsetQualifiedName<PdfToolset>(nameof(PdfToolset.SearchPdfText))
        ];
        var expected = new Dictionary<string, (string Picture, string Setup, string[] Additional)>
        {
            ["researcher"] = ("researcher-elyndra-alien",
                "Start with supplied text, files or URLs. For web discovery, configure and enable an existing search tool in the editor. For Library retrieval, select Library under Workspaces and complete indexing and provider setup.",
                [ToolNaming.BuildToolsetQualifiedName(null, "web", "fetch"), ToolNaming.BuildToolsetQualifiedName(null, "datetime", "get_current")]),
            ["study-coach"] = ("tutor-mirell-alien",
                "Supply a topic or learning material. Practice stays in this conversation, without stored progress or reminders.",
                []),
            ["interview-coach"] = ("translator-selyth-alien",
                "Supply role details or start generic practice. Employer research is not automatic.",
                []),
            ["writing-brand-editor"] = ("writer-ovelis-alien",
                "Supply text and any style guide. Revisions appear in chat. File writing needs tools and access you enable later.",
                []),
            ["document-analyst"] = ("data-analyst-synthra-alien",
                "Supply text or readable files. For Library retrieval, select Library under Workspaces and complete indexing and provider setup.",
                []),
            ["meeting-analyst"] = ("news-curator-veyra-alien",
                "Supply notes or a transcript. This agent does not attend, record, create tasks or send summaries.",
                []),
            ["planning-assistant"] = ("project-planner-talorin-alien",
                "Supply goals and constraints. Optional Google Calendar and Microsoft To Do reads need connected accounts. Plans do not change events or tasks.",
                [ToolNaming.BuildToolsetQualifiedName(null, "datetime", "get_current"), ToolNaming.BuildToolsetQualifiedName("oscova.google-workspace", "googlecalendar", "list"), ToolNaming.BuildToolsetQualifiedName("oscova.google-workspace", "googlecalendar", "get"), ToolNaming.BuildToolsetQualifiedName("oscova.microsoft-graph", "microsofttodo", "list_task_lists"), ToolNaming.BuildToolsetQualifiedName("oscova.microsoft-graph", "microsofttodo", "list_tasks")]),
            ["email-assistant"] = ("email-assistant-temeris-alien",
                "Paste messages or connect Google or Microsoft for inbox reads. Replies stay as text drafts in chat. Sending and mailbox changes are not enabled.",
                [ToolNaming.BuildToolsetQualifiedName("oscova.google-workspace", "gmail", "search_emails"), ToolNaming.BuildToolsetQualifiedName("oscova.google-workspace", "gmail", "read_email"), ToolNaming.BuildToolsetQualifiedName("oscova.microsoft-graph", "outlook", "list_emails"), ToolNaming.BuildToolsetQualifiedName("oscova.microsoft-graph", "outlook", "search_emails"), ToolNaming.BuildToolsetQualifiedName("oscova.microsoft-graph", "outlook", "read_email")]),
        };
        foreach (var template in plugin.AgentTemplates)
        {
            using var stream = plugin.Assembly.GetManifestResourceStream(
                $"Oscova.Agents.Plugins.AgentTemplates.Templates.{template.Id}.md");
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream);
            var (frontmatter, body) = MarkdownFrontmatter.Split(reader.ReadToEnd());
            var file = new YamlConverter().Deserialize<AgentDefinitionFile>(frontmatter)!;
            Assert.Equal(template.Id, file.Id);
            Assert.Equal(file.Name, template.Name);
            Assert.Equal(file.Description, template.Description);
            Assert.Equal(body, template.SystemInstruction);
            var baseline = expected[template.Id];
            Assert.Equal(baseline.Picture, template.ProfilePictureKey);
            Assert.Equal(baseline.Setup, template.SetupInstructions);
            Assert.Equal(common.Concat(baseline.Additional), template.RequestedToolNames);
        }
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void Package_ContainsOnlyTemplatePluginAndFrameworkDependency()
    {
        var feed = Environment.GetEnvironmentVariable("OSCOVA_NUGET_PACKAGE_DIRECTORY")
            ?? throw new InvalidOperationException("Set OSCOVA_NUGET_PACKAGE_DIRECTORY to the reviewed local feed.");
        using var package = new PackageArchiveReader(Path.Combine(feed,
            $"Oscova.Agents.Plugins.AgentTemplates.{PackageVersions.Get("Oscova.Agents.Plugins.AgentTemplates")}.nupkg"));
        var files = package.GetFiles().ToArray();
        Assert.Contains("oscova-plugin.json", files);
        Assert.Contains("lib/net10.0/Oscova.Agents.Plugins.AgentTemplates.dll", files);
        Assert.DoesNotContain(files, file => file.EndsWith("/Oscova.Agents.dll", StringComparison.Ordinal) ||
            file.Contains("Oscova.Agents.Core", StringComparison.Ordinal) ||
            file.Contains("Oscova.Agents.Infrastructure", StringComparison.Ordinal) ||
            file.Contains("Oscova.Agents.Host", StringComparison.Ordinal));
        var dependency = Assert.Single(package.NuspecReader.GetDependencyGroups().SelectMany(group => group.Packages));
        Assert.Equal("Oscova.Agents", dependency.Id);
    }
}
