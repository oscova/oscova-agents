using Microsoft.Extensions.AI;
using Oscova.Agents.Policies;
using System.Runtime.CompilerServices;

namespace Oscova.Agents.Core.Conversation;

/// <summary>
/// Records a built agent's resolved model provider into the ambient egress ledger.
/// </summary>
/// <remarks>
/// It rebinds the run's agent id for the inner client, so usage, served-provider and fallback egress land on the
/// agent that owns this client. Only the model client sees the rebinding; function invocation keeps its own binding.
/// </remarks>
public sealed class EgressCaptureChatClient : DelegatingChatClient
{
    private readonly ProviderEgressDescriptor _descriptor;
    private readonly RunContextAccessor _runContextAccessor;
    private readonly bool _recordProviderEgress;

    private EgressCaptureChatClient(
        IChatClient inner,
        ProviderEgressDescriptor descriptor,
        RunContextAccessor runContextAccessor,
        bool recordProviderEgress) : base(inner)
    {
        _descriptor = descriptor;
        _runContextAccessor = runContextAccessor;
        _recordProviderEgress = recordProviderEgress;
    }

    /// <summary>
    /// Creates a wrapper that records the resolved provider and scopes the current agent id.
    /// </summary>
    public static EgressCaptureChatClient RecordProvider(
        IChatClient inner,
        ProviderEgressDescriptor descriptor,
        RunContextAccessor runContextAccessor) =>
        new(inner, descriptor, runContextAccessor, recordProviderEgress: true);

    /// <summary>
    /// Creates a wrapper that scopes the current agent id while leaving provider recording to the inner client.
    /// </summary>
    public static EgressCaptureChatClient ScopeAgentOnly(
        IChatClient inner,
        ProviderEgressDescriptor descriptor,
        RunContextAccessor runContextAccessor) =>
        new(inner, descriptor, runContextAccessor, recordProviderEgress: false);

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var context = _runContextAccessor.Current;
        if (_recordProviderEgress)
        {
            context?.EgressLedger?.Record(_descriptor);
        }

        if (context?.EgressLedger is null)
        {
            return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }

        var scope = _runContextAccessor.SetContext(context.With(currentAgentId: _descriptor.AgentId));
        await using (scope.ConfigureAwait(false))
        {
            return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var context = _runContextAccessor.Current;
        if (_recordProviderEgress)
        {
            context?.EgressLedger?.Record(_descriptor);
        }

        if (context?.EgressLedger is null)
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }

            yield break;
        }

        var scope = _runContextAccessor.SetContext(context.With(currentAgentId: _descriptor.AgentId));
        await using (scope.ConfigureAwait(false))
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
        }
    }
}
