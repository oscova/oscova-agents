using Oscova.Agents.Conversations;

namespace Oscova.Agents.Core.ApiContracts.Chat;

/// <summary>Supplies display history and composer state without transferring the private model session.</summary>
public sealed class ChatThreadViewResponse
{
    /// <summary>Gets the loaded display window and conversation metadata.</summary>
    public ChatThread Thread { get; init; } = new();
    /// <summary>Gets or sets whether an earlier display page exists.</summary>
    public bool HasOlderMessages { get; set; }
    /// <summary>Gets or sets whether the server retains a model session for inspection and compaction.</summary>
    public bool HasSession { get; set; }
    /// <summary>Gets or sets whether a stopped saved run can continue.</summary>
    public bool CanContinue { get; set; }
    /// <summary>Gets or sets the saved recipient used to estimate a stopped continuation.</summary>
    public string? ContinuationRecipientAgentId { get; set; }
    /// <summary>Gets or sets the model provider last chosen for this chat, when one was chosen.</summary>
    public Guid? ModelProviderId { get; set; }
}
