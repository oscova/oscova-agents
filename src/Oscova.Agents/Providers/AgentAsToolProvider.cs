using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Oscova.Agents.Collections;
using Oscova.Agents.Utils;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Providers;

/// <summary>
/// Provides agent-as-tool functions from the child agents resolved for the parent build.
/// </summary>
public class AgentAsToolProvider(IEnumerable<string> agentToolIds, AIAgentCollection agents) : AIContextProvider
{
    private readonly IReadOnlyList<ChatClientAgent> _agents =
    [
        .. agentToolIds
            .Select(agentId => agents.TryGetAgentById(agentId, out var agent) ? agent : null)
            .OfType<ChatClientAgent>()
    ];

    private readonly ProviderSessionState<State> _sessionState = new(
        static _ => new State(),
        nameof(AgentAsToolProvider));

    /// <inheritdoc />
    protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        var tools = new List<AITool>(_agents.Count);

        foreach (var agent in _agents)
        {
            tools.Add(CreateAgentFunction(agent, context.Session));
        }

        return new ValueTask<AIContext>(new AIContext { Tools = tools });
    }

    /// <summary>Reads the delegated conversations recorded in a parent session.</summary>
    public async ValueTask<IReadOnlyList<AgentToolConversation>> GetConversationsAsync(
        AIAgent sessionOwner,
        AgentSession parentSession,
        CancellationToken cancellationToken = default)
    {
        var state = _sessionState.GetOrInitializeState(parentSession);
        var conversations = new List<AgentToolConversation>(state.Conversations.Count);
        foreach (var entry in state.Conversations)
        {
            var session = await sessionOwner
                .DeserializeSessionAsync(entry.Session, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var messages = session.TryGetInMemoryChatHistory(out var history) ? history : [];
            conversations.Add(new AgentToolConversation(entry.AgentId, entry.Name, messages));
        }

        return conversations;
    }

    private AIFunction CreateAgentFunction(ChatClientAgent agent, AgentSession? parentSession)
    {
        [Description("Invoke an agent to retrieve some information.")]
        async Task<string> InvokeAgentAsync(
            [Description("Input query to invoke the agent.")] string query,
            [Description("Optional name for a separate line of work with this agent. Reuse the same name to continue that conversation, or pick a new name to start a separate one.")] string? conversation = null,
            CancellationToken cancellationToken = default)
        {
            var runOptions = FunctionInvokingChatClient.CurrentContext?.Options?.AdditionalProperties is { } properties
                ? new AgentRunOptions { AdditionalProperties = properties }
                : null;

            if (parentSession is null)
            {
                var stateless = await agent
                    .RunAsync(query, options: runOptions, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                return stateless.Text;
            }

            var name = string.IsNullOrWhiteSpace(conversation) ? null : conversation.Trim();
            var state = _sessionState.GetOrInitializeState(parentSession);
            var entry = state.Find(agent.Id, name);
            var childSession = entry is null
                ? await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false)
                : await agent.DeserializeSessionAsync(entry.Session, cancellationToken: cancellationToken).ConfigureAwait(false);

            var response = await agent
                .RunAsync(query, session: childSession, options: runOptions, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            // Storing after the run keeps a failed or cancelled call from replacing the last good snapshot.
            state.Store(
                agent.Id,
                name,
                await agent.SerializeSessionAsync(childSession, cancellationToken: cancellationToken).ConfigureAwait(false));
            _sessionState.SaveState(parentSession, state);
            return response.Text;
        }

        return AIFunctionFactory.Create(InvokeAgentAsync, new AIFunctionFactoryOptions
        {
            Name = agent.Name is null ? null : AgentCallableName.Normalize(agent.Name),
            Description = agent.Description
        });
    }

    /// <summary>Stores one serialized child session per called agent and conversation name.</summary>
    private sealed class State
    {
        public List<Entry> Conversations { get; init; } = [];

        public Entry? Find(string agentId, string? name) =>
            Conversations.FirstOrDefault(entry =>
                string.Equals(entry.AgentId, agentId, StringComparison.Ordinal) &&
                string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));

        public void Store(string agentId, string? name, JsonElement session)
        {
            var existing = Find(agentId, name);
            if (existing is null)
            {
                Conversations.Add(new Entry { AgentId = agentId, Name = name, Session = session });
                return;
            }

            existing.Session = session;
        }

        public sealed class Entry
        {
            public string AgentId { get; set; } = string.Empty;
            public string? Name { get; set; }
            public JsonElement Session { get; set; }
        }
    }
}
