using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using StreamingChatCompletionUpdate = OpenAI.Chat.StreamingChatCompletionUpdate;
using ChatCompletion = OpenAI.Chat.ChatCompletion;

namespace Oscova.Agents.Infrastructure.Chat;

/// <summary>
/// Recovers reasoning text that OpenAI-compatible endpoints (notably OpenRouter) return in the
/// non-standard <c>reasoning</c> field. The Microsoft.Extensions.AI OpenAI adapter only maps
/// <c>reasoning_content</c> (DeepSeek/vLLM style), so without this the reasoning arrives as empty
/// text content and is dropped instead of rendering as the assistant's thinking.
/// </summary>
internal sealed class ReasoningRecoveryChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var message in response.Messages)
        {
            // Preserve provider reasoning for nonstreaming task history without duplicating SDK-mapped content.
#pragma warning disable SCME0001
            if (!message.Contents.OfType<TextReasoningContent>().Any() && message.RawRepresentation is ChatCompletion raw &&
                raw.Patch.TryGetValue("$.choices[0].message.reasoning"u8, out string? reasoning) &&
                !string.IsNullOrEmpty(reasoning))
#pragma warning restore SCME0001
            {
                message.Contents.Add(new TextReasoningContent(reasoning));
            }
        }

        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            // Empty deltas can arrive between reasoning chunks and must not split saved reasoning.
            for (var i = update.Contents.Count - 1; i >= 0; i--)
            {
                if (update.Contents[i] is TextContent textContent &&
                    string.IsNullOrEmpty(textContent.Text))
                {
                    update.Contents.RemoveAt(i);
                }
            }

            // Patch is the evaluation-gated raw-JSON accessor MEAI itself uses to read these fields.
#pragma warning disable SCME0001
            if (update.RawRepresentation is StreamingChatCompletionUpdate raw &&
                raw.Patch.TryGetValue("$.choices[0].delta.reasoning"u8, out string? reasoning) &&
                !string.IsNullOrEmpty(reasoning))
#pragma warning restore SCME0001
            {
                update.Contents.Add(new TextReasoningContent(reasoning));
            }

            yield return update;
        }
    }
}
