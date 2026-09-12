using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Oscova.Agents.Policies;
using Oscova.Agents.Providers;
using Oscova.Agents.Runtime;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using OscovaSummarizingChatReducer = Oscova.Agents.Reducers.SummarizingChatReducer;

namespace Oscova.Agents.Tests.Framework.Runtime;

public sealed class AgentOrchestratorTests
{
    [Fact]
    public void SyncTools_AddsToolsetsAndExternalTools()
    {
        var orchestrator = new AgentOrchestrator();
        var externalTool = CreateExternalTool();

        orchestrator.SyncTools(
            [new ToolsetRegistration(new TestToolset())],
            [externalTool],
            new Dictionary<string, ExternalToolMetadata>
            {
                [externalTool.Name] = new("external-server", "search")
            });

        Assert.Equal(2, orchestrator.Tools.Count);
        Assert.NotNull(orchestrator.Tools.Find("test__ping"));
        Assert.NotNull(orchestrator.Tools.Find("external_search"));
        Assert.Equal("external-server", orchestrator.Tools.GetServerName("external_search"));
        Assert.Equal("search", orchestrator.Tools.GetOriginalName("external_search"));
    }

    [Theory]
    [InlineData(NetworkScope.None, true, true)]
    [InlineData(NetworkScope.Incidental, true, true)]
    [InlineData(NetworkScope.Egress, true, false)]
    [InlineData(NetworkScope.Egress, false, true)]
    public void SyncTools_HonorsDeclaredMcpNetworkScope(NetworkScope networkScope, bool airGapped, bool isAvailable)
    {
        var orchestrator = new AgentOrchestrator();
        var externalTool = CreateExternalTool();
        orchestrator.SyncTools(
            [],
            [externalTool],
            new Dictionary<string, ExternalToolMetadata>
            {
                [externalTool.Name] = new("Public MCP", "search", networkScope)
            });

        var resolver = new AgentToolResolver(orchestrator.Tools, airGapped: airGapped);

        Assert.Equal(isAvailable, resolver.Resolve(externalTool.Name) is not null);
    }

    [Fact]
    public void SyncTools_WhenExternalToolCollidesWithReflectedTool_RejectsReplacement()
    {
        var orchestrator = new AgentOrchestrator();
        var externalTool = CreateExternalTool("collision__write_file");

        var exception = Assert.Throws<InvalidOperationException>(() => orchestrator.SyncTools(
            [new ToolsetRegistration(new CollisionToolset())],
            [externalTool],
            new Dictionary<string, ExternalToolMetadata>
            {
                [externalTool.Name] = new("collision", "write_file")
            }));

        Assert.Contains("collision__write_file", exception.Message, StringComparison.Ordinal);
        Assert.Empty(orchestrator.Tools);
    }

    [Fact]
    public async Task GetOrCreateAgentAsync_CachesBuiltAgent()
    {
        var orchestrator = new AgentOrchestrator();
        var definition = new AgentDefinition { Id = "main", Name = "Main" };
        var request = CreateRequest(definition, [definition]);

        ChatClientAgent first = await orchestrator.GetOrCreateAgentAsync(request);
        ChatClientAgent second = await orchestrator.GetOrCreateAgentAsync(request);

        Assert.Same(first, second);
        Assert.Single(orchestrator.AIAgents);
    }

    [Fact]
    public async Task GetOrCreateAgentAsync_BuildsExposedAgentDependenciesFirst()
    {
        var orchestrator = new AgentOrchestrator();
        var child = new AgentDefinition
        {
            Id = "child",
            Name = "Child",
            IsExposedAsTool = true
        };
        var main = new AgentDefinition
        {
            Id = "main",
            Name = "Main",
            AgentToolIds = [child.Id]
        };
        var buildOrder = new List<string>();
        var request = CreateRequest(main, [main, child], definition => buildOrder.Add(definition.Id));

        ChatClientAgent agent = await orchestrator.GetOrCreateAgentAsync(request);

        Assert.Equal(main.Id, agent.Id);
        Assert.Equal([child.Id, main.Id], buildOrder);
        Assert.True(orchestrator.AIAgents.Contains(child.Id));
        Assert.True(orchestrator.AIAgents.Contains(main.Id));
    }

    [Fact]
    public async Task GetOrCreateAgentAsync_WhenCachedExposedAgentIsNoLongerAvailable_DoesNotExposeIt()
    {
        var orchestrator = new AgentOrchestrator();
        var chatClient = new DummyChatClient();
        var child = new AgentDefinition
        {
            Id = "child",
            Name = "Child",
            IsExposedAsTool = true
        };
        var main = new AgentDefinition
        {
            Id = "main",
            Name = "Main",
            AgentToolIds = [child.Id]
        };
        var request = new AgentBuildRequest(
            (_, _) => Task.FromResult<IChatClient>(chatClient),
            main,
            [main, child],
            (definition, _) => Task.FromResult<IReadOnlyList<AIContextProvider>>(
                definition.Id == main.Id
                    ? [new AgentAsToolProvider(definition.AgentToolIds, orchestrator.AIAgents)]
                    : []),
            _ => []);

        await orchestrator.GetOrCreateAgentAsync(request);
        ChatClientAgent rebuilt = await orchestrator.GetOrCreateAgentAsync(request with
        {
            AvailableAgentDefinitions = [main],
            ConfigureBuilder = builder => builder
        });
        await rebuilt.RunAsync(new ChatMessage(ChatRole.User, "test"));

        Assert.True(chatClient.WasCalled);
        Assert.Empty(chatClient.LastOptions?.Tools ?? []);
    }

    [Fact]
    public async Task GetOrCreateAgentAsync_WhenConcurrentRunReplacesExposedChild_KeepsOriginalRunChildPolicy()
    {
        var orchestrator = new AgentOrchestrator();
        orchestrator.SyncTools(
            [new ToolsetRegistration(new EgressToolset())],
            [],
            new Dictionary<string, ExternalToolMetadata>());
        string egressToolName = Assert.Single(orchestrator.Tools).Name;
        var child = new AgentDefinition
        {
            Id = "child",
            Name = "Child",
            IsExposedAsTool = true
        };
        var airGappedParent = new AgentDefinition
        {
            Id = "air-gapped-parent",
            Name = "Air Gapped Parent",
            AgentToolIds = [child.Id]
        };
        var ordinaryParent = new AgentDefinition
        {
            Id = "ordinary-parent",
            Name = "Ordinary Parent",
            AgentToolIds = [child.Id]
        };
        AgentDefinition[] availableDefinitions = [airGappedParent, ordinaryParent, child];
        var airGappedParentClient = new DummyChatClient();
        var airGappedChildClient = new DummyChatClient();
        var ordinaryParentClient = new DummyChatClient();
        var ordinaryChildClient = new DummyChatClient();
        var airGappedParentProviderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAirGappedParentProvider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        AgentBuildRequest CreateRunRequest(
            AgentDefinition parent,
            DummyChatClient parentClient,
            DummyChatClient childClient,
            bool airGapped)
        {
            return new AgentBuildRequest(
                (definition, _) => Task.FromResult<IChatClient>(
                    definition.Id == child.Id ? childClient : parentClient),
                parent,
                availableDefinitions,
                async (definition, _) =>
                {
                    if (definition.Id == airGappedParent.Id)
                    {
                        airGappedParentProviderStarted.SetResult();
                        await releaseAirGappedParentProvider.Task;
                    }

                    return definition.Id == parent.Id
                        ? [new AgentAsToolProvider(definition.AgentToolIds, orchestrator.AIAgents)]
                        : [new AgentToolProvider(
                            [egressToolName],
                            orchestrator.Tools,
                            airGapped: airGapped)];
                },
                _ => [],
                ConfigureAgentBuilder: static (_, builder) => builder);
        }

        AgentBuildRequest airGappedRequest = CreateRunRequest(
            airGappedParent,
            airGappedParentClient,
            airGappedChildClient,
            airGapped: true);
        AgentBuildRequest ordinaryRequest = CreateRunRequest(
            ordinaryParent,
            ordinaryParentClient,
            ordinaryChildClient,
            airGapped: false);

        Task<ChatClientAgent> airGappedBuild = orchestrator.GetOrCreateAgentAsync(airGappedRequest);
        await airGappedParentProviderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<ChatClientAgent> ordinaryBuild = orchestrator.GetOrCreateAgentAsync(ordinaryRequest);
        releaseAirGappedParentProvider.SetResult();
        await ordinaryBuild;
        ChatClientAgent airGappedAgent = await airGappedBuild;
        await airGappedAgent.RunAsync(new ChatMessage(ChatRole.User, "Invoke the child."));
        AIFunction childFunction = Assert.IsAssignableFrom<AIFunction>(
            Assert.Single(airGappedParentClient.LastOptions?.Tools ?? []));

        await childFunction.InvokeAsync(new AIFunctionArguments { ["query"] = "Check policy." });

        Assert.DoesNotContain(
            ordinaryChildClient.LastOptions?.Tools ?? [],
            tool => string.Equals(tool.Name, egressToolName, StringComparison.Ordinal));
        Assert.True(airGappedChildClient.WasCalled);
        Assert.False(ordinaryChildClient.WasCalled);
    }

    [Fact]
    public async Task GetOrCreateAgentAsync_AppliesCustomizersOnlyToRequestedAgent()
    {
        var orchestrator = new AgentOrchestrator();
        var child = new AgentDefinition
        {
            Id = "child",
            Name = "Child",
            IsExposedAsTool = true
        };
        var main = new AgentDefinition
        {
            Id = "main",
            Name = "Main",
            AgentToolIds = [child.Id]
        };
        var configureBuilderCount = 0;
        var configureOptionsCount = 0;

        var request = new AgentBuildRequest(
            (_, _) => Task.FromResult<IChatClient>(new DummyChatClient()),
            main,
            [main, child],
            (agentDefinition, _) => Task.FromResult<IReadOnlyList<AIContextProvider>>([]),
            _ => [],
            builder =>
            {
                configureBuilderCount++;
                return builder;
            },
            _ => configureOptionsCount++);

        await orchestrator.GetOrCreateAgentAsync(request);

        Assert.Equal(1, configureBuilderCount);
        Assert.Equal(1, configureOptionsCount);
    }

    [Fact]
    public async Task GetOrCreateAgentAsync_WhenRequestHasCustomizers_DoesNotReuseCachedAgent()
    {
        var orchestrator = new AgentOrchestrator();
        var definition = new AgentDefinition { Id = "main", Name = "Main" };
        var cachedRequest = CreateRequest(definition, [definition]);
        var customizeCount = 0;
        var customizedRequest = new AgentBuildRequest(
            (_, _) => Task.FromResult<IChatClient>(new DummyChatClient()),
            definition,
            [definition],
            (_, _) => Task.FromResult<IReadOnlyList<AIContextProvider>>([]),
            _ => [],
            builder =>
            {
                customizeCount++;
                return builder;
            });

        ChatClientAgent cached = await orchestrator.GetOrCreateAgentAsync(cachedRequest);
        ChatClientAgent customized = await orchestrator.GetOrCreateAgentAsync(customizedRequest);

        Assert.NotSame(cached, customized);
        Assert.Equal(1, customizeCount);
        Assert.Same(cached, await orchestrator.GetOrCreateAgentAsync(cachedRequest));
        Assert.Single(orchestrator.AIAgents);
    }

    [Fact]
    public async Task GetOrCreateAgentAsync_ReturnsOneCachedAgentForConcurrentMisses()
    {
        var orchestrator = new AgentOrchestrator();
        var definition = new AgentDefinition { Id = "main", Name = "Main" };
        var request = new AgentBuildRequest(
            (_, _) => Task.FromResult<IChatClient>(new DummyChatClient()),
            definition,
            [definition],
            async (_, _) =>
            {
                await Task.Yield();
                return [];
            },
            _ => []);

        ChatClientAgent[] agents = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => orchestrator.GetOrCreateAgentAsync(request)));

        Assert.Single(agents.Distinct());
        Assert.Single(orchestrator.AIAgents);
    }

    [Fact]
    public async Task GetOrCreateAgentAsync_WhenBypassRequestedAgentCache_DoesNotReuseRequestedAgent()
    {
        var orchestrator = new AgentOrchestrator();
        var definition = new AgentDefinition { Id = "main", Name = "Main" };
        var cachedRequest = CreateRequest(definition, [definition]);
        var bypassRequest = cachedRequest with { BypassRequestedAgentCache = true };

        ChatClientAgent cached = await orchestrator.GetOrCreateAgentAsync(cachedRequest);
        ChatClientAgent bypassed = await orchestrator.GetOrCreateAgentAsync(bypassRequest);

        Assert.NotSame(cached, bypassed);
        Assert.Same(cached, await orchestrator.GetOrCreateAgentAsync(cachedRequest));
        Assert.Single(orchestrator.AIAgents);
    }

    [Fact]
    public async Task GetOrCreateAgentAsync_WhenCompactionEnabled_AttachesSummarizingReducer()
    {
        var orchestrator = new AgentOrchestrator();
        var definition = new AgentDefinition
        {
            Id = "main",
            Name = "Main",
            IsCompactionEnabled = true
        };
        var request = CreateRequest(definition, [definition]) with
        {
            ChatReducerFactory = (agentDefinition, chatClient, _) =>
            {
                var budget = OscovaSummarizingChatReducer.DeriveHistoryBudget(4096, null);
                return Task.FromResult<IChatReducer?>(new OscovaSummarizingChatReducer(chatClient, budget));
            }
        };

        ChatClientAgent agent = await orchestrator.GetOrCreateAgentAsync(request);

        var provider = Assert.IsType<InMemoryChatHistoryProvider>(agent.ChatHistoryProvider);
        var reducer = Assert.IsType<OscovaSummarizingChatReducer>(provider.ChatReducer);
        Assert.Equal(OscovaSummarizingChatReducer.MinimumHistoryBudget, reducer.MaxTokenCount);
    }

    [Fact]
    public async Task GetOrCreateAgentAsync_WhenCompactionDisabled_LeavesReducerUnset()
    {
        var orchestrator = new AgentOrchestrator();
        var definition = new AgentDefinition
        {
            Id = "main",
            Name = "Main",
            IsCompactionEnabled = false
        };
        var request = CreateRequest(definition, [definition]);

        ChatClientAgent agent = await orchestrator.GetOrCreateAgentAsync(request);

        var provider = Assert.IsType<InMemoryChatHistoryProvider>(agent.ChatHistoryProvider);
        Assert.Null(provider.ChatReducer);
    }

    [Fact]
    public void SyncTools_NormalizesExplicitExternalToolMetadata()
    {
        var orchestrator = new AgentOrchestrator();
        var externalTool = CreateExternalTool();

        orchestrator.SyncTools(
            [],
            [externalTool],
            new Dictionary<string, ExternalToolMetadata>
            {
                [externalTool.Name] = new("External Server", "Search Items")
            });

        Assert.Equal("external_server", orchestrator.Tools.GetServerName(externalTool));
        Assert.Equal("search_items", orchestrator.Tools.GetOriginalName(externalTool));
        Assert.Same(externalTool, Assert.Single(orchestrator.Tools.GetToolsByServer("External Server")));
    }

    private static AgentBuildRequest CreateRequest(
        AgentDefinition definition,
        IReadOnlyList<AgentDefinition> availableDefinitions,
        Action<AgentDefinition>? onBuild = null)
    {
        return new AgentBuildRequest(
            (_, _) => Task.FromResult<IChatClient>(new DummyChatClient()),
            definition,
            availableDefinitions,
            (agentDefinition, _) =>
            {
                onBuild?.Invoke(agentDefinition);
                return Task.FromResult<IReadOnlyList<AIContextProvider>>([]);
            },
            _ => []);
    }

    private static AIFunction CreateExternalTool(string name = "external_search")
    {
        MethodInfo method = typeof(AgentOrchestratorTests)
            .GetMethod(nameof(ExternalSearch), BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("External search method was not found.");

        return AIFunctionFactory.Create(
            method,
            target: null,
            options: new AIFunctionFactoryOptions { Name = name });
    }

    private static string ExternalSearch() => "result";

    private sealed class TestToolset : ICrewToolset
    {
        public static string Ping() => "pong";
    }

    private sealed class CollisionToolset : ICrewToolset
    {
        [DisplayName("write_file")]
        [ToolPolicy(ApprovalMode = ToolApprovalMode.RequireApproval)]
        public static string WriteFile() => "reflected";
    }

    private sealed class EgressToolset : ICrewToolset
    {
        [DisplayName("marker")]
        [ToolNetworkScope(NetworkScope.Egress)]
        public static string Marker() => "egress";
    }

    private sealed class DummyChatClient : IChatClient
    {
        public ChatOptions? LastOptions { get; private set; }
        public bool WasCalled { get; private set; }

        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            LastOptions = options;
            return Task.FromResult(new ChatResponse());
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            LastOptions = options;
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }
    }
}
