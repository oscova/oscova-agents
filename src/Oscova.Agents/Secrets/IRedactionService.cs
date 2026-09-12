using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace Oscova.Agents.Secrets;

/// <summary>Masks secrets before transcripts, tool arguments, or run artifacts leave the execution boundary.</summary>
public interface IRedactionService
{
    /// <summary>Masks the user's stored secret values when user-scoped credentials are available.</summary>
    Task<string> RedactTextAsync(string text, string? userId = null, CancellationToken ct = default);

    /// <summary>Returns a key-name and credential-value redacted copy of tool-call arguments.</summary>
    Task<IDictionary<string, object?>> RedactArgumentsAsync(
        IDictionary<string, object?> arguments,
        string? userId = null,
        CancellationToken ct = default);

    /// <summary>Returns redacted copies of chat messages without mutating the input messages.</summary>
    Task<IReadOnlyList<ChatMessage>> RedactMessagesAsync(
        IReadOnlyList<ChatMessage> messages,
        string? userId = null,
        CancellationToken ct = default);
}
