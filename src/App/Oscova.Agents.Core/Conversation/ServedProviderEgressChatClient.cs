using Microsoft.Extensions.AI;
using Oscova.Agents.Policies;
using System.Runtime.CompilerServices;

namespace Oscova.Agents.Core.Conversation;

/// <summary>
/// Records build-time fallback attempts and successful response activation for the current model call.
/// </summary>
public sealed class ServedProviderEgressChatClient(
    IChatClient inner,
    EgressClass egressClass,
    string providerId,
    string? modelId,
    RunContextAccessor runContextAccessor) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Record();
        var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        // Activation confirms a response, while egress records the attempt.
        runContextAccessor.Current?.FallbackSignal?.MarkActivated();
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Record();
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            runContextAccessor.Current?.FallbackSignal?.MarkActivated();
            yield return update;
        }

        runContextAccessor.Current?.FallbackSignal?.MarkActivated();
    }

    private void Record()
    {
        var context = runContextAccessor.Current;
        if (context?.EgressLedger is null)
        {
            return;
        }

        context.EgressLedger.Record(new ProviderEgressDescriptor(
            context.CurrentAgentId ?? string.Empty,
            egressClass,
            providerId,
            modelId));
    }
}
