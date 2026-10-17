using Oscova.Agents.Persistence;
using Oscova.Agents.Toolsets;

namespace Oscova.Agents.Plugins.AgentTemplates;

/// <summary>Contributes the eight curated starters and their declared defaults.</summary>
public sealed class AgentTemplatesPlugin : IAgentPlugin
{
    private static readonly string[] Templates =
    [
        "researcher", "study-coach", "interview-coach", "writing-brand-editor",
        "document-analyst", "meeting-analyst", "planning-assistant", "email-assistant"
    ];

    /// <summary>Registers embedded starters in their curated display order.</summary>
    public void Configure(AgentPluginBuilder builder)
    {
        var yaml = new YamlConverter();
        foreach (var id in Templates)
        {
            using var stream = typeof(AgentTemplatesPlugin).Assembly.GetManifestResourceStream(
                $"Oscova.Agents.Plugins.AgentTemplates.Templates.{id}.md")
                ?? throw new InvalidDataException($"Template '{id}' is missing its embedded instructions.");
            using var reader = new StreamReader(stream);
            var (frontmatter, body) = MarkdownFrontmatter.Split(reader.ReadToEnd());
            var file = yaml.Deserialize<TemplateMetadata>(frontmatter);
            if (file is null || file.Id != id)
            {
                throw new InvalidDataException($"Template '{id}' has mismatched metadata.");
            }
            builder.AddAgentTemplate(new AgentTemplateDefinition
            {
                Id = id,
                Name = file.Name,
                Description = file.Description,
                SystemInstruction = body,
                SetupInstructions = file.SetupInstructions,
                RequestedToolNames =
                [
                    ToolNaming.BuildToolsetQualifiedName<CodingToolset>(nameof(CodingToolset.ReadFile)),
                    ToolNaming.BuildToolsetQualifiedName<PdfToolset>(nameof(PdfToolset.ExtractPdfText)),
                    ToolNaming.BuildToolsetQualifiedName<PdfToolset>(nameof(PdfToolset.SearchPdfText)),
                    .. file.AdditionalTools.Select(tool =>
                        ToolNaming.BuildToolsetQualifiedName(tool.PluginId, tool.Toolset, tool.Tool))
                ],
                ProfilePictureKey = file.ProfilePictureKey
            });
        }
    }
}

/// <summary>Reads starter-only frontmatter without extending persisted agent files.</summary>
internal sealed class TemplateMetadata
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SetupInstructions { get; set; } = string.Empty;
    public string ProfilePictureKey { get; set; } = string.Empty;
    public List<TemplateTool> AdditionalTools { get; set; } = [];
}

/// <summary>Stores the canonical pieces of an optional tool identity.</summary>
internal sealed class TemplateTool
{
    public string? PluginId { get; set; }
    public string Toolset { get; set; } = string.Empty;
    public string Tool { get; set; } = string.Empty;
}
