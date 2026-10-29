namespace Oscova.Agents.Core.ApiContracts.Agents;

/// <summary>Describes the estimated initial input assembled from an agent configuration.</summary>
public sealed class AgentInstructionPreview
{
    public string AgentId { get; set; } = string.Empty;
    public string Instructions { get; set; } = string.Empty;
    public int EstimatedInputTokens { get; set; }
    public int InstructionTokens { get; set; }
    public int MessageTokens { get; set; }
    public int ToolTokens { get; set; }
    public int ToolAssemblyAdjustmentTokens { get; set; }
    public string ProviderName { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public int ContextWindowSize { get; set; }
    public int ReservedOutputTokens { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
    public IReadOnlyList<AgentContextContribution> Contributions { get; set; } = [];
    public IReadOnlyList<AgentContextTool> Tools { get; set; } = [];
}

/// <summary>Attributes the assembled instruction and message cost to one context provider.</summary>
public sealed class AgentContextContribution
{
    public string Source { get; set; } = string.Empty;
    public int InstructionTokens { get; set; }
    public int MessageTokens { get; set; }
}

/// <summary>Explains one initial or deferred declaration using its canonical parameter schema.</summary>
public sealed class AgentContextTool
{
    public string Source { get; set; } = string.Empty;
    public string ServerName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string JsonSchema { get; set; } = string.Empty;
    public int EstimatedTokens { get; set; }
    public bool IsDeferred { get; set; }
}
