using Microsoft.Extensions.AI;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Oscova.Agents.Infrastructure.Chat;

/// <summary>Replays DeepSeek reasoning without replacing the SDK's message and tool serialization.</summary>
internal sealed class DeepSeekReasoningChatClient(
    IChatClient inner,
    Func<IReadOnlyList<ChatMessage>, IChatClient> createClient) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var history = messages.ToArray();
        using var client = createClient(history);
        return await client.GetResponseAsync(history, options, cancellationToken).ConfigureAwait(false);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var history = messages.ToArray();
        using var client = createClient(history);
        await foreach (var update in client.GetStreamingResponseAsync(history, options, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    /// <summary>Adds vendor fields to the completed SDK request using that call's reasoning history.</summary>
    internal sealed class RequestPolicy(IReadOnlyList<ChatMessage> history, bool thinkingEnabled) : PipelinePolicy
    {
        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            ShapeRequest(message);
            ProcessNext(message, pipeline, currentIndex);
        }

        public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            ShapeRequest(message);
            await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
        }

        private void ShapeRequest(PipelineMessage message)
        {
            using var stream = new MemoryStream();
            message.Request.Content!.WriteTo(stream, CancellationToken.None);
            stream.Position = 0;
            var body = JsonNode.Parse(stream)!.AsObject();
            body["thinking"] = new JsonObject { ["type"] = thinkingEnabled ? "enabled" : "disabled" };
            if (!thinkingEnabled)
            {
                body.Remove("reasoning_effort");
            }

            if (body["max_completion_tokens"] is { } maxTokens)
            {
                body["max_tokens"] = maxTokens.DeepClone();
                body.Remove("max_completion_tokens");
            }

            var reasoning = history.Where(item => item.Role == ChatRole.Assistant)
                .Select(item => string.Concat(item.Contents.OfType<TextReasoningContent>().Select(content => content.Text)))
                .ToArray();
            var assistantIndex = 0;
            foreach (var item in body["messages"]!.AsArray())
            {
                var assistant = item!.AsObject();
                if (assistant["role"]!.GetValue<string>() != "assistant") { continue; }

                if (thinkingEnabled)
                {
                    // Keep generated reasoning in full, including assistant turns without a tool call.
                    var text = reasoning[assistantIndex++];
                    if (text.Length > 0 || assistant["reasoning_content"] is null)
                    {
                        assistant["reasoning_content"] = text;
                    }
                }
                else
                {
                    assistant.Remove("reasoning_content");
                }
            }

            message.Request.Content = BinaryContent.Create(BinaryData.FromString(body.ToJsonString()));
        }
    }
}
