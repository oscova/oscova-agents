using Microsoft.Extensions.AI;
using Oscova.Agents.Core.ApiContracts.Audit;

namespace Oscova.Agents.Core.ApiContracts.Chat;

/// <summary>
/// Carries the fresh persisted session state needed by the session inspector.
/// </summary>
public sealed class ChatSessionInspectorSnapshot
{
    /// <summary>
    /// Gets or sets the model-session messages for the thread.
    /// </summary>
    public IReadOnlyList<ChatMessage> Messages { get; set; } = [];

    /// <summary>
    /// Gets or sets the stored system instruction captured for the session.
    /// </summary>
    public string? SystemInstruction { get; set; }

    /// <summary>
    /// Gets or sets audited egress entries recorded for the inspected chat thread.
    /// </summary>
    public IReadOnlyList<EgressAuditEntry> EgressEntries { get; set; } = [];
}
