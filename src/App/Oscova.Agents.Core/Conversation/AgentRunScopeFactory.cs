using Microsoft.Extensions.AI;
using Oscova.Agents.Definitions;
using Oscova.Agents.Runtime;
using Oscova.Agents.Workspace;
using Oscova.Agents.Core.Settings;
using System.Runtime.CompilerServices;

namespace Oscova.Agents.Core.Conversation;

/// <summary>
/// Re-establishes app-owned run context for each agent's own run.
/// </summary>
public sealed class AgentRunScopeFactory(
    RunContextAccessor runContextAccessor,
    PrivacySettings privacySettings) : IAgentRunScopeFactory
{
    /// <summary>Returns whether ambient, user, or agent policy requires local-only execution.</summary>
    public static bool RequiresLocalExecution(
        bool ambientAirGapped,
        bool userLocalOnly,
        bool agentLocalOnly) => ambientAirGapped || userLocalOnly || agentLocalOnly;

    /// <inheritdoc />
    public ChatClientBuilder Apply(
        ChatClientBuilder builder,
        AgentDefinition definition,
        Func<IReadOnlyList<AllowedDirectory>> workspaceResolver)
    {
        bool agentLocalOnly = definition.IsLocalOnly;

        // Keep the agent scope active while the composed client invokes tools.
        return new ChatClientBuilder(services => new ScopedRunContextChatClient(
            builder.Build(services),
            runContextAccessor,
            privacySettings,
            definition.Id,
            definition.Name,
            agentLocalOnly,
            workspaceResolver));
    }

    private sealed class ScopedRunContextChatClient(
        IChatClient inner,
        RunContextAccessor runContextAccessor,
        PrivacySettings privacySettings,
        string agentId,
        string agentName,
        bool agentLocalOnly,
        Func<IReadOnlyList<AllowedDirectory>> workspaceResolver) : DelegatingChatClient(inner)
    {
        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var current = runContextAccessor.Current;
            if (current is null)
            {
                return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            }

            using var ownedConfiguration = current.ConfigurationScope is null ? new Oscova.Agents.Core.Auth.ExecutionPermissionRegistry.ConfigurationScope(current.UserId, cancellationToken) : null;
            var configuration = current.ConfigurationScope ?? ownedConfiguration!;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, configuration.Token);
            var scope = runContextAccessor.SetContext(CreateAgentContext(current.With(configurationScope: configuration)));
            await using (scope.ConfigureAwait(false))
            {
                return await base.GetResponseAsync(messages, options, linked.Token).ConfigureAwait(false);
            }
        }
        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var current = runContextAccessor.Current;
            if (current is null)
            {
                await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
                {
                    yield return update;
                }

                yield break;
            }

            using var ownedConfiguration = current.ConfigurationScope is null ? new Oscova.Agents.Core.Auth.ExecutionPermissionRegistry.ConfigurationScope(current.UserId, cancellationToken) : null;
            var configuration = current.ConfigurationScope ?? ownedConfiguration!;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, configuration.Token);
            var agentContext = CreateAgentContext(current.With(configurationScope: configuration));
            IAsyncEnumerator<ChatResponseUpdate>? enumerator = null;
            try
            {
                while (true)
                {
                    ChatResponseUpdate update;
                    // Each iterator advance resumes under the caller's execution context.
                    var scope = runContextAccessor.SetContext(agentContext);
                    await using (scope.ConfigureAwait(false))
                    {
                        enumerator ??= base.GetStreamingResponseAsync(messages, options, linked.Token)
                            .GetAsyncEnumerator(linked.Token);
                        if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) { yield break; }

                        update = enumerator.Current;
                    }

                    yield return update;
                }
            }
            finally
            {
                if (enumerator is not null)
                {
                    var scope = runContextAccessor.SetContext(agentContext);
                    await using (scope.ConfigureAwait(false))
                    {
                        await enumerator.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
        }
        /// <summary>Aligns identity, file resolution, and offline policy with the built agent.</summary>
        private RunContext CreateAgentContext(RunContext current) => current.With(
            airGapped: RequiresLocalExecution(current.AirGapped, privacySettings.IsLocalOnly, agentLocalOnly),
            // Attribute delegated usage to the agent executing this client.
            currentAgentName: agentName,
            currentAgentId: agentId,
            allowedDirectories: current.DelegationAgentIds.Count > 0 && current.CurrentAgentId == agentId
                ? current.AllowedDirectories
                : workspaceResolver());
    }
}
