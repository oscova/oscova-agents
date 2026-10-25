using System.Reflection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Oscova.Agents.Collections;
using Oscova.Agents.Policies;
using Oscova.Agents.Providers;
using Oscova.Agents.Tests.Framework.TestTools;

namespace Oscova.Agents.Tests.Framework.Providers;

public sealed class ToolSearchProviderTests
{

    /// <summary>Empty discovery instructions remain exact while the search tool stays available.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(" \t\n")]
    public async Task ProvideAIContextAsync_WithEmptyInstructions_KeepsEmptyInstructions(string instructions)
    {
        var tools = new AgentToolCollection();
        var provider = new ToolSearchProvider([], [], tools, new AgentToolResolver(tools), new LexicalToolSearchStrategy(), instructions);
        var context = await InvokeProviderAsync(provider);
        Assert.Equal(instructions, context.Instructions);
        Assert.Equal("search_tools", Assert.Single(context.Tools!).Name);
    }

    /// <summary>Inspection exposes only unpinned functions permitted by policy and offline mode.</summary>
    [Fact]
    public void GetSearchableTools_RespectsPinsPoliciesAndEgress()
    {
        var pinned = AIFunctionFactory.Create(() => true, "pinned");
        var unpinned = AIFunctionFactory.Create(() => true, "unpinned");
        var denied = AIFunctionFactory.Create(() => true, "denied");
        var egress = AIFunctionFactory.Create(() => true, "egress");
        var tools = new AgentToolCollection { pinned, unpinned, denied };
        tools.Add(egress, "external", egress.Name, NetworkScope.Egress);
        var resolver = new AgentToolResolver(tools,
            [new AgentToolPolicy { ToolName = denied.Name, ApprovalMode = ToolApprovalMode.Deny }],
            airGapped: true);
        var provider = new ToolSearchProvider(
            tools.Select(tool => tool.Name), [pinned.Name], tools, resolver, new LexicalToolSearchStrategy());

        Assert.Same(unpinned, Assert.Single(provider.GetSearchableTools()));
    }

    [Fact]
    public async Task ProvideAIContextAsync_ReturnsOnlySearchToolBeforeUnlock()
    {
        var provider = CreateProvider(out _);

        var context = await InvokeProviderAsync(provider);

        var tool = Assert.Single(context.Tools!);
        Assert.Equal("search_tools", tool.Name);
        Assert.Contains("hidden behind `search_tools`", context.Instructions);
    }

    /// <summary>Called tools return in their thread, including with a rebuilt agent, but never leak to a new thread.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProvideAIContextAsync_RestoresCalledToolsOnlyForTheirThread(bool rebuildAgent)
    {
        var pinned = AIFunctionFactory.Create(() => true, "pinned");
        var called = AIFunctionFactory.Create(() => true, "hidden");
        var unused = AIFunctionFactory.Create(() => true, "hidden_companion");
        var tools = new AgentToolCollection { pinned, called, unused };
        var requests = 0;
        using var client = new TestChatClient
        {
            ResponseAsync = (_, options, _) =>
            {
                requests++;
                if (requests == 1)
                {
                    Assert.Equal([ToolSearchProvider.SearchToolName, pinned.Name], options!.Tools!.Select(tool => tool.Name));
                    return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                        [new FunctionCallContent("search", ToolSearchProvider.SearchToolName,
                            new Dictionary<string, object?> { ["query"] = "hidden" })])));
                }

                if (requests == 2)
                {
                    Assert.Contains(options!.Tools!, tool => tool.Name == called.Name);
                    Assert.Contains(options.Tools!, tool => tool.Name == unused.Name);
                    return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                        [new FunctionCallContent("call", called.Name)])));
                }

                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
            }
        };
        ChatClientAgent CreateAgent() => new(client, new ChatClientAgentOptions
        {
            Id = "same-agent",
            AIContextProviders = [new ToolSearchProvider(tools.Select(tool => tool.Name), [pinned.Name],
                tools, new AgentToolResolver(tools), new LexicalToolSearchStrategy())]
        });
        var agent = CreateAgent();
        var firstThread = await agent.CreateSessionAsync();

        await agent.RunAsync("Use the hidden capability", firstThread);
        Assert.True(firstThread.TryGetInMemoryChatHistory(out var history));
        Assert.Contains(history.SelectMany(message => message.Contents).OfType<FunctionCallContent>(), call => call.Name == called.Name);

        if (rebuildAgent) { agent = CreateAgent(); }

        var newThread = await agent.CreateSessionAsync();
        await agent.RunAsync("Start fresh", newThread);
        Assert.Equal([ToolSearchProvider.SearchToolName, pinned.Name],
            client.ResponseOptions[^1]!.Tools!.Select(tool => tool.Name));

        await agent.RunAsync("Continue", firstThread);
        Assert.Equal([ToolSearchProvider.SearchToolName, pinned.Name, called.Name],
            client.ResponseOptions[^1]!.Tools!.Select(tool => tool.Name));

        await agent.RunAsync("Continue again", firstThread);
        Assert.Equal([ToolSearchProvider.SearchToolName, pinned.Name, called.Name],
            client.ResponseOptions[^1]!.Tools!.Select(tool => tool.Name));
    }

    /// <summary>An approval resume keeps tools that search loaded earlier in the same turn, including across a generated wake.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProvideAIContextAsync_ApprovalResumeKeepsToolsLoadedThisTurn(bool afterGeneratedWake)
    {
        var gated = new ApprovalRequiredAIFunction(AIFunctionFactory.Create(() => true, "hidden_gated"));
        var companion = AIFunctionFactory.Create(() => true, "hidden_companion");
        var tools = new AgentToolCollection { gated, companion };
        var replies = new Queue<AIContent>();
        replies.Enqueue(new FunctionCallContent("search", ToolSearchProvider.SearchToolName,
            new Dictionary<string, object?> { ["query"] = "hidden" }));
        if (afterGeneratedWake) { replies.Enqueue(new TextContent("waiting")); }
        replies.Enqueue(new FunctionCallContent("gated", gated.Name));
        replies.Enqueue(new FunctionCallContent("companion", companion.Name));
        var companionRequest = replies.Count - 1;
        using var client = new TestChatClient
        {
            ResponseAsync = (_, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [replies.Count > 0 ? replies.Dequeue() : new TextContent("done")])))
        };
        var wake = new WakeContextProvider();
        var agent = new ChatClientAgent(client, new ChatClientAgentOptions
        {
            // Production history keeps generated-context attribution on stored messages.
            ChatHistoryProvider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
            {
                ProvideOutputMessageFilter = ChatMessageOrigin.CopyHistoryMetadata
            }),
            AIContextProviders = [wake, new ToolSearchProvider(tools.Select(tool => tool.Name), [],
                tools, new AgentToolResolver(tools), new LexicalToolSearchStrategy())]
        });
        var thread = await agent.CreateSessionAsync();

        var paused = await agent.RunAsync("Use the hidden capabilities", thread);
        if (afterGeneratedWake)
        {
            // A worker result wakes the same turn as generated user-role context.
            wake.Pending = new ChatMessage(ChatRole.User, "Subagent update (not from the user)");
            paused = await agent.RunAsync([], thread);
        }

        var approval = Assert.Single(paused.Messages.SelectMany(message => message.Contents).OfType<ToolApprovalRequestContent>());

        await agent.RunAsync(new ChatMessage(ChatRole.User, [approval.CreateResponse(true)]), thread);

        Assert.Contains(client.ResponseOptions[companionRequest]!.Tools!, tool => tool.Name == companion.Name);
    }

    [Fact]
    public async Task ProvideAIContextAsync_PinnedToolIsExposedAndExcludedFromSearch()
    {
        var qualifier = ToolNaming.GetToolsetOwner(typeof(SimpleTestTool));
        var pinnedToolName = ToolNaming.BuildQualifiedName(qualifier, "PlaySong");
        var pinnedProvider = CreateProvider(out string playSongName, out string turnOnMusicName, pinnedToolName);

        var context = await InvokeProviderAsync(pinnedProvider);
        var searchResult = InvokeSearchTools(pinnedProvider, "play song");
        var unpinnedSearchResult = InvokeSearchTools(pinnedProvider, "embedded music player");

        Assert.Contains(context.Tools!, tool => tool.Name == "search_tools");
        Assert.Contains(context.Tools!, tool => tool.Name == playSongName);
        Assert.DoesNotContain(playSongName, searchResult);
        Assert.Contains(turnOnMusicName, unpinnedSearchResult);
    }

    private sealed class WakeContextProvider : AIContextProvider
    {
        public ChatMessage? Pending { get; set; }

        protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default)
        {
            var messages = Pending is null ? null : new[] { Pending };
            Pending = null;
            return new ValueTask<AIContext>(new AIContext { Messages = messages });
        }
    }

    private static ToolSearchProvider CreateProvider(out string playSongName, string? pinnedToolName = null)
    {
        return CreateProvider(out playSongName, out _, pinnedToolName);
    }

    private static ToolSearchProvider CreateProvider(
        out string playSongName,
        out string turnOnMusicName,
        string? pinnedToolName = null)
    {
        var collection = new AgentToolCollection
        {
            new SimpleTestTool()
        };
        var qualifier = ToolNaming.GetToolsetOwner(typeof(SimpleTestTool));
        var allowedToolNames = collection.Select(tool => tool.Name).ToList();
        playSongName = ToolNaming.BuildQualifiedName(qualifier, "PlaySong");
        turnOnMusicName = ToolNaming.BuildQualifiedName(qualifier, "TurnOnMusic");
        return new ToolSearchProvider(
            allowedToolNames,
            pinnedToolName is null ? [] : [pinnedToolName],
            collection,
            new AgentToolResolver(collection),
            new LexicalToolSearchStrategy());
    }

    private static string InvokeSearchTools(ToolSearchProvider provider, string query)
    {
        var method = typeof(ToolSearchProvider).GetMethod(
            "SearchTools",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        return Assert.IsType<string>(method.Invoke(provider, [query]));
    }

    private static async Task<AIContext> InvokeProviderAsync(AIContextProvider provider)
    {
        var method = typeof(ToolSearchProvider).GetMethod(
            "ProvideAIContextAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var result = method.Invoke(provider, [null!, CancellationToken.None]);
        var contextTask = Assert.IsType<ValueTask<AIContext>>(result);
        return await contextTask.ConfigureAwait(false);
    }
}
