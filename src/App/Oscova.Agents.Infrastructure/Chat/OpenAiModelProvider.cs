using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI.Responses;
using Oscova.Agents.Credentials;
using Oscova.Agents.Llm;
using System.ClientModel;
using System.Runtime.CompilerServices;

namespace Oscova.Agents.Infrastructure.Chat;

/// <summary>
/// OpenAI model provider that uses the Responses API for chat and OpenAI embeddings.
/// </summary>
[CredentialSlot(Key = "openai", Label = "OpenAI API Key", Description = "API key used by OpenAI model and speech providers.")]
public sealed class OpenAiModelProvider(
    ILogger<OpenAiModelProvider> logger,
    IHttpClientFactory httpClientFactory) : OpenAiCompatibleModelProviderBase(logger, httpClientFactory)
{
    private static readonly Uri DefaultEndpointUriValue = new("https://api.openai.com/v1/");

    public override string Type => ModelProviderKeys.OpenAI;

    protected override Uri? DefaultEndpointUri => DefaultEndpointUriValue;

    /// <inheritdoc />
    public override ValueTask<IChatClient> CreateClientAsync(
        ModelProviderConfig config,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        var model = ResolveModelId(config);
        var credential = new ApiKeyCredential(ResolveApiKeyOrThrow(apiKey));
        var inner = new ResponsesFailureChatClient(BuildResponsesClient(model, credential, config));
        var configured = inner.AsBuilder()
            .ConfigureOptions(options => ApplyResponsesRequestDefaults(options, config))
            .Build();

        return ValueTask.FromResult(configured);
    }

    // Responses API surfaces reasoning summary deltas natively as TextReasoningContent.
    // OPENAI001 gates the Responses API as evaluation-only; opting in is the SDK's intended path.
#pragma warning disable OPENAI001
    private IChatClient BuildResponsesClient(string model, ApiKeyCredential credential, ModelProviderConfig config)
    {
        var options = new ResponsesClientOptions();
        if (TryResolveEndpoint(config, out var endpoint) && endpoint is not null)
        {
            options.Endpoint = endpoint;
        }

        return new ResponsesClient(credential, options).AsIChatClient(model);
    }

    /// <summary>Propagates failed Responses events through the shared fallback and run-failure paths.</summary>
    private sealed class ResponsesFailureChatClient(IChatClient inner) : DelegatingChatClient(inner)
    {
        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            {
                // Refusals and incomplete responses have distinct event types and retain their existing behavior.
                if (update.RawRepresentation is StreamingResponseFailedUpdate or StreamingResponseErrorUpdate)
                {
                    var message = update.RawRepresentation is StreamingResponseFailedUpdate failed
                        ? failed.Response.Error?.Message
                        : update.Contents.OfType<ErrorContent>().FirstOrDefault()?.Message;
                    throw new ClientResultException(message ?? "The model provider reported an error in its response stream.");
                }

                yield return update;
            }
        }
    }

    private void ApplyResponsesRequestDefaults(ChatOptions options, ModelProviderConfig config)
    {
        options.MaxOutputTokens ??= config.ChatOptions.MaxOutputTokens;
        // Internal calls inherit saved effort, while explicit per-call options retain precedence.
        options.Reasoning ??= config.ChatOptions.Clone().Reasoning;
        ApplyRequestDefaults(options, config);

        // Disabled reasoning cannot produce a thinking summary.
        if (options.Reasoning?.Effort == ReasoningEffort.None)
        {
            options.Reasoning.Output = null;
        }
        else if (config.SupportsThinking)
        {
            options.Reasoning!.Effort ??= ReasoningEffort.Medium;
            options.Reasoning.Output ??= ReasoningOutput.Full;
        }

        options.Temperature = null;

        // The Responses API stores conversation state server-side by default. The agent owns
        // chat history locally, so disable stored output on every provider-created client.
        options.RawRepresentationFactory ??= _ => new CreateResponseOptions { StoredOutputEnabled = false };
    }
#pragma warning restore OPENAI001
}
