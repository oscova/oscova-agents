namespace Oscova.Agents.Core.ApiContracts.Prompts;

/// <summary>Carries the updated content for a prompt file.</summary>
public sealed class SavePromptRequest
{
    public string Content { get; set; } = string.Empty;
    public string? Mode { get; set; }
}
