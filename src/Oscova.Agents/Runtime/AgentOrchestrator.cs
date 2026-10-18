using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Definitions;
using Oscova.Agents.Collections;
using Oscova.Agents.Conversations;
using Oscova.Agents.Providers;
using Oscova.Agents.Middleware;
using Oscova.Agents.Workspace;
using OscovaSummarizingChatReducer = Oscova.Agents.Reducers.SummarizingChatReducer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Runtime;

/// <summary>
/// Builds and caches agents from host-prepared runtime inputs.
/// </summary>
/// <remarks>
/// The orchestrator owns framework-level tool synchronization and cache invalidation; hosts prepare model clients, definitions, and context providers.
/// </remarks>
public sealed class AgentOrchestrator(IAgentRunScopeFactory? runScopeFactory = null) : IAgentOrchestrator
{
    private readonly SemaphoreSlim _agentBuildGate = new(1, 1);

    /// <inheritdoc />
    public AgentToolCollection Tools { get; } = [];

    /// <inheritdoc />
    public AIAgentCollection AIAgents { get; } = [];

    /// <inheritdoc />
    public void SyncTools(
        IEnumerable<ToolsetRegistration> toolsets,
        IReadOnlyList<AIFunction> externalTools,
        IReadOnlyDictionary<string, ExternalToolMetadata> externalToolMetadata,
        Func<AIFunction, AIFunction>? decorate = null)
    {
        // Publish a complete replacement so active readers never observe a partial tool rebuild.
        var rebuiltTools = new AgentToolCollection();
        foreach (var registration in toolsets)
        {
            rebuiltTools.Add(registration.Toolset, registration.Origin);
        }
        foreach (var tool in externalTools)
        {
            if (externalToolMetadata.TryGetValue(tool.Name, out var metadata))
            {
                rebuiltTools.Add(tool, metadata.ServerName, metadata.OriginalName, metadata.NetworkScope);
                continue;
            }
            rebuiltTools.Add(tool);
        }
        if (decorate is not null) { rebuiltTools.Decorate(decorate); }
        Tools.ReplaceWith(rebuiltTools);
    }

    /// <inheritdoc />
    public void EvictAgent(string agentId)
    {
        AIAgents.Remove(agentId);
    }

    /// <inheritdoc />
    public void InvalidateAllAgents() => AIAgents.Clear();

    /// <inheritdoc />
    public void InvalidateAll()
    {
        Tools.Clear();
        AIAgents.Clear();
    }

    /// <inheritdoc />
    public async Task<ChatClientAgent> GetOrCreateAgentAsync(
        AgentBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        var hasRequestCustomizers = request.ConfigureBuilder is not null ||
                                    request.ConfigureOptions is not null ||
                                    request.ConfigureAgentBuilder is not null;
        // Exposed agents have a restricted provider shape when used as tools; direct builds must not share that cache entry.
        var canUseCachedAgent = !request.AgentDefinition.IsExposedAsTool &&
                                !hasRequestCustomizers &&
                                !request.BypassRequestedAgentCache;
        if (canUseCachedAgent && AIAgents.TryGetAgentById(request.AgentDefinition.Id, out var cached))
        {
            return cached!;
        }

        // Dependency replacement and parent provider capture must stay in one build transaction.
        await _agentBuildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureExposedAgentToolsBuiltAsync(request, cancellationToken).ConfigureAwait(false);
            return await BuildAgentAsync(
                request,
                request.AgentDefinition,
                applyRequestCustomizers: true,
                cacheAgent: canUseCachedAgent,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _agentBuildGate.Release();
        }
    }

    private async Task EnsureExposedAgentToolsBuiltAsync(
        AgentBuildRequest request,
        CancellationToken cancellationToken)
    {
        if (request.AgentDefinition.AgentToolIds.Count == 0)
        {
            return;
        }

        var building = new HashSet<string> { request.AgentDefinition.Id };
        foreach (var agentToolId in request.AgentDefinition.AgentToolIds)
        {
            await EnsureExposedAgentToolBuiltAsync(request, agentToolId, building, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnsureExposedAgentToolBuiltAsync(
        AgentBuildRequest request,
        string agentId,
        HashSet<string> building,
        CancellationToken cancellationToken)
    {
        if (!building.Add(agentId))
        {
            return;
        }

        var definition = request.AvailableAgentDefinitions
            .FirstOrDefault(agent => agent.Id == agentId && agent.IsExposedAsTool);
        if (definition is null)
        {
            // Current visibility must win over a stale per-user cache after shared-agent revocation.
            AIAgents.Remove(agentId);
            return;
        }

        var replaceCachedAgent = request.ConfigureAgentBuilder is not null;
        if (AIAgents.Contains(agentId) && !replaceCachedAgent)
        {
            return;
        }

        foreach (var childAgentId in definition.AgentToolIds)
        {
            await EnsureExposedAgentToolBuiltAsync(request, childAgentId, building, cancellationToken).ConfigureAwait(false);
        }

        var agent = await BuildAgentAsync(
            request,
            definition,
            applyRequestCustomizers: false,
            cacheAgent: !replaceCachedAgent,
            cancellationToken).ConfigureAwait(false);
        if (replaceCachedAgent)
        {
            // A run must replace dependencies cached earlier by preview or session-inspection paths without host middleware.
            AIAgents.Add(agent);
        }
    }

    private async Task<ChatClientAgent> BuildAgentAsync(
        AgentBuildRequest request,
        AgentDefinition definition,
        bool applyRequestCustomizers,
        bool cacheAgent,
        CancellationToken cancellationToken)
    {
        var providers = await request.ContextProviderFactory(definition, cancellationToken).ConfigureAwait(false);
        var chatClient = await request.ChatClientFactory(definition, cancellationToken).ConfigureAwait(false);
        var options = definition.ToChatClientAgentOptions(providers);
        options.ChatOptions = request.ChatOptionsResolver?.Invoke(definition)?.Clone();

        IChatReducer? reducer = null;
        if (definition.IsCompactionEnabled)
        {
            reducer = request.ChatReducerFactory is null
                ? new OscovaSummarizingChatReducer(chatClient)
                : await request.ChatReducerFactory(definition, chatClient, cancellationToken).ConfigureAwait(false);
        }

        // Preserve stored origin before history retrieval stamps its outgoing message copies.
        options.ChatHistoryProvider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
        {
            ChatReducer = reducer,
            ReducerTriggerEvent = InMemoryChatHistoryProviderOptions.ChatReducerTriggerEvent.AfterMessageAdded,
            ProvideOutputMessageFilter = ChatMessageOrigin.CopyHistoryMetadata
        });

        if (applyRequestCustomizers)
        {
            request.ConfigureOptions?.Invoke(options);
        }

        // Compose all function-invocation middleware onto a single FunctionInvokingChatClient.
        // Each UseFunctionInvocation call adds a separate FICC, and an outer FICC buffers every
        // streaming update from the first tool call to end of stream - which stalls the post-tool
        // answer until the run completes. One shared FICC keeps the stream flowing.
        var functionInvocationConfigurators = new List<System.Action<FunctionInvokingChatClient>>
        {
            // Explicit caps pin framework defaults. Detailed tool exceptions stay out of model-visible
            // results; application code can still inspect FunctionResultContent.Exception.
            static fic =>
            {
                fic.MaximumIterationsPerRequest = 40;
                fic.MaximumConsecutiveErrorsPerRequest = 3;
                fic.IncludeDetailedErrors = false;
            }
        };
        var builder = chatClient.AsBuilder()
            .Use((innerClient, services) =>
            {
                var loggerFactory = services.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
                var fic = new GovernedFunctionInvokingChatClient(innerClient, loggerFactory, services);
                foreach (var configure in functionInvocationConfigurators)
                {
                    configure(fic);
                }
                return fic;
            })
            // Below the shared FICC so every model call in the tool loop drains pending steered messages.
            .UseMessageInjection();

        // Validation and ambient host resolution must use the same built-agent workspace policy.
        Func<IReadOnlyList<AllowedDirectory>> workspaceResolver = () => request.WorkspaceResolver(definition);
        using (FunctionInvocationComposition.Begin(functionInvocationConfigurators))
        {
            builder = builder.UseWorkspacePathValidation(workspaceResolver);

            if (request.ConfigureAgentBuilder is not null)
            {
                builder = request.ConfigureAgentBuilder(definition, builder);
            }

            if (applyRequestCustomizers && request.ConfigureBuilder is not null)
            {
                builder = request.ConfigureBuilder(builder);
            }

            // Register last so failure handling wraps governance and accounting.
            if (request.ConfigureHarnessBuilder is not null)
            {
                builder = request.ConfigureHarnessBuilder(builder);
            }
        }

        if (options.ChatHistoryProvider is InMemoryChatHistoryProvider { ChatReducer: OscovaSummarizingChatReducer summarizingReducer } &&
            options.AIContextProviders?.Select(provider => provider.GetService<TodoProvider>()).FirstOrDefault(provider => provider is not null) is { } todos)
        {
            var restoration = new TodoRestorationProvider(todos);
            summarizingReducer.OnHistoryReduced += restoration.MarkPending;
            builder = builder.UseAIContextProviders(restoration);
        }

        // Persist restored todo context as well as injected messages for subsequent tool iterations.
        builder = builder.UsePerServiceCallChatHistoryPersistence();

        if (runScopeFactory is not null)
        {
            builder = runScopeFactory.Apply(
                builder,
                definition,
                workspaceResolver);
        }

        // The agent takes this pipeline as-is, so the approval middleware it would add is composed here.
        var composed = builder;
        builder = new ChatClientBuilder(services => composed.Build(services))
            .UseApprovalResponseBinding()
            .UseApprovalNotRequiredFunctionBypassing();
        options.UseProvidedChatClientAsIs = true;
        options.RequirePerServiceCallChatHistoryPersistence = true;

        var agent = builder.BuildAIAgent(options);
        if (!cacheAgent)
        {
            return agent;
        }

        return AIAgents.GetOrAdd(agent);
    }
}
