using Oscova.Agents.Core.ApiContracts.Agents;
using Oscova.Agents.Llm;
using Oscova.Agents.Memory;
using Oscova.Agents.Providers;

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

#pragma warning disable MAAI001

namespace Oscova.Agents.Core.Conversation;

/// <summary>
/// Renders context-provider instructions without sending a request to a model.
/// </summary>
public static class AgentInstructionPreviewRenderer
{
    /// <summary>
    /// Runs the supplied context providers through the public framework invocation pipeline.
    /// </summary>
    public static async Task<string> RenderAsync(
        IReadOnlyList<AIContextProvider> providers,
        CancellationToken cancellationToken = default)
    {
        var preview = await RenderContextAsync(
            providers,
            messages: null,
            invokingAgent: null,
            session: null,
            cancellationToken).ConfigureAwait(false);

        return preview.Instructions;
    }

    /// <summary>Runs providers once and records their initial context contributions.</summary>
    public static async Task<AgentContextPreview> RenderContextAsync(
        IReadOnlyList<AIContextProvider> providers,
        IEnumerable<ChatMessage>? messages = null,
        AIAgent? invokingAgent = null,
        AgentSession? session = null,
        CancellationToken cancellationToken = default)
    {
        var agent = invokingAgent ?? new PreviewAgent();
        session ??= await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        var context = new AIContext { Messages = messages?.ToList() ?? [] };
        var contributions = new List<AgentContextContribution>();
        var toolContributions = new List<AgentContextToolContribution>();

        foreach (var provider in providers)
        {
            var priorInstructions = ContextWindowTokenEstimator.EstimateText(context.Instructions);
            var priorMessages = ContextWindowTokenEstimator.EstimateMessages(context.Messages ?? []);
            var priorToolCount = context.Tools?.Count() ?? 0;
            context = await provider.InvokingAsync(
                new AIContextProvider.InvokingContext(agent, session, context),
                cancellationToken).ConfigureAwait(false);
            context.Messages = context.Messages?.ToList() ?? [];
            context.Tools = context.Tools?.ToList() ?? [];
            var source = GetSource(provider);

            // Cumulative differences include separators and tokenizer boundary effects exactly once.
            contributions.Add(new AgentContextContribution
            {
                Source = source,
                InstructionTokens = ContextWindowTokenEstimator.EstimateText(context.Instructions) - priorInstructions,
                MessageTokens = ContextWindowTokenEstimator.EstimateMessages(context.Messages) - priorMessages
            });
            toolContributions.Add(new AgentContextToolContribution(
                source,
                context.Tools.Skip(priorToolCount).ToList(),
                provider is ToolSearchProvider search ? search.GetSearchableTools() : []));
        }

        return new AgentContextPreview(
            context.Instructions ?? string.Empty,
            context.Messages?.ToList() ?? [],
            context.Tools?.ToList() ?? [],
            contributions,
            toolContributions);
    }

    private static string GetSource(AIContextProvider provider) => provider switch
    {
        EnvironmentContextProvider => "runtime",
        AgentPersonalContextProvider => "instructions",
        ToolSearchProvider => "discovery",
        AgentToolProvider => "tools",
        PrivateNotesContextProvider => "notes",
        AgentAsToolProvider => "agents",
        AgentSkillsProvider => "skills",
        MemoryContextProvider => "memory",
        LibraryContextProvider => "library",
        TodoProvider or OmitEmptyTodoListProvider => "todo",
        VisionToolProvider => "vision",
        _ => "other"
    };
    private sealed class PreviewAgent : AIAgent
    {
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<AgentSession>(new PreviewSession());
        }

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }));
        }

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<AgentSession>(new PreviewSession());
        }

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AgentResponse([]));
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }
    }

    private sealed class PreviewSession : AgentSession;
}

/// <summary>Captures provider output and its attribution without sending a model request.</summary>
public sealed record AgentContextPreview(
    string Instructions,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<AITool> Tools,
    IReadOnlyList<AgentContextContribution> Contributions,
    IReadOnlyList<AgentContextToolContribution> ToolContributions);

/// <summary>Retains real declarations for metadata attribution and local estimation.</summary>
public sealed record AgentContextToolContribution(
    string Source,
    IReadOnlyList<AITool> InitialTools,
    IReadOnlyList<AIFunction> DeferredTools);

#pragma warning restore MAAI001
