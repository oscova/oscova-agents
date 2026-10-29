using Oscova.Agents.Persistence;

namespace Oscova.Agents.Core.Conversation;

/// <summary>Keeps a sidebar instruction mode and custom body in one Markdown document.</summary>
public static class PromptInstructionRecord
{
    private const string MissingMetadataMessage = "Instruction metadata is missing. Declare instruction_mode as default, append, or replace.";
    /// <summary>Reads composition metadata without treating the Markdown body as configuration.</summary>
    public static (string Mode, string Content) Parse(string document)
    {
        var (yaml, body) = MarkdownFrontmatter.Split(document);
        // A managed instruction must declare its composition mode before its body can be used.
        if (string.IsNullOrWhiteSpace(yaml))
        {
            throw new InvalidOperationException(MissingMetadataMessage);
        }

        var metadata = new YamlConverter().Deserialize<Metadata>(yaml);
        if (metadata is null || string.IsNullOrWhiteSpace(metadata.InstructionMode))
        {
            throw new InvalidOperationException(MissingMetadataMessage);
        }

        InstructionSelection.Validate(metadata.InstructionMode);
        return (metadata.InstructionMode, body);
    }

    /// <summary>Writes the selected mode and unchanged custom text into one document.</summary>
    public static string Format(string? mode, string content)
    {
        InstructionSelection.Validate(mode);
        return MarkdownFrontmatter.Combine(new YamlConverter().Serialize(new Metadata { InstructionMode = mode! }), content);
    }

    /// <summary>Describes the sidebar record's composition metadata.</summary>
    public sealed class Metadata
    {
        public string InstructionMode { get; set; } = string.Empty;
    }
}
