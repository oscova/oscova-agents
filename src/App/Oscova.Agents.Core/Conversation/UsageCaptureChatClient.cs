using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;

namespace Oscova.Agents.Core.Conversation;

/// <summary>
/// Records model token usage from each response into the ambient usage ledger.
/// </summary>
public sealed class UsageCaptureChatClient(
    IChatClient inner,
    Guid providerId,
    string modelId,
    decimal? inputCostPerMillionTokens,
    decimal? outputCostPerMillionTokens,
    RunContextAccessor runContextAccessor) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        Record(response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount, response.Usage?.TotalTokenCount);

        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var reportedUsage = false;
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            foreach (var content in update.Contents)
            {
                if (content is UsageContent usageContent)
                {
                    reportedUsage = true;
                    var details = usageContent.Details;
                    Record(details.InputTokenCount, details.OutputTokenCount, details.TotalTokenCount);
                }
            }

            yield return update;
        }

        if (!reportedUsage) { Record(null, null, null); }
    }

    private void Record(long? inputTokens, long? outputTokens, long? totalTokens)
    {
        var context = runContextAccessor.Current;
        // Keep the configured rates with this response so later edits do not reprice its usage.
        context?.UsageLedger?.Add(
            context.CurrentAgentId ?? string.Empty,
            providerId,
            modelId,
            inputCostPerMillionTokens,
            outputCostPerMillionTokens,
            inputTokens,
            outputTokens,
            totalTokens);
    }
}
