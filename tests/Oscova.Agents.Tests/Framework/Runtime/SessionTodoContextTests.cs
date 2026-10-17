using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Oscova.Agents.Runtime;
using OscovaSummarizingChatReducer = Oscova.Agents.Reducers.SummarizingChatReducer;

namespace Oscova.Agents.Tests.Framework.Runtime;

/// <summary>Exercises saved todo context through real history replay and compaction.</summary>
public sealed class SessionTodoContextTests
{
    /// <summary>History replay preserves stored authorship before and after session serialization.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolLoop_PreservesStoredProviderOrigin(bool compaction)
    {
        using var todos = new TodoProvider();
        var requests = new List<IReadOnlyList<ChatMessage>>();
        var tool = AIFunctionFactory.Create(() => "receipt", "lookup");
        using var model = new TestChatClient
        {
            ResponseAsync = (messages, _, _) =>
            {
                requests.Add(messages.ToList());
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    requests.Count == 1
                        ? [new FunctionCallContent("lookup-1", tool.Name, new Dictionary<string, object?>())]
                        : [new TextContent("done")])));
            }
        };
        var agent = await BuildAsync(model, todos, compaction, [tool]);
        var session = await agent.CreateSessionAsync();
        var request = new ChatMessage(ChatRole.User, "Compare the quotes")
        {
            AdditionalProperties = new() { ["request-label"] = "customer" }
        };

        await agent.RunAsync([request], session);

        Assert.Equal(2, requests.Count);
        Assert.Single(requests[1], message => message.Role == ChatRole.User && message.Text != request.Text);
        var history = Assert.IsType<InMemoryChatHistoryProvider>(agent.ChatHistoryProvider);
        var generated = Assert.Single(history.GetMessages(session), message => message.Role == ChatRole.User && message.Text != request.Text);
        Assert.Equal(AgentRequestMessageSourceType.AIContextProvider, generated.GetAgentRequestMessageSourceType());
        Assert.Equal(AgentRequestMessageSourceType.External, request.GetAgentRequestMessageSourceType());
        Assert.Contains(requests[1], message => message.Text == generated.Text && message.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.ChatHistory);
        var restored = await agent.DeserializeSessionAsync(await agent.SerializeSessionAsync(session));
        var restoredGenerated = Assert.Single(history.GetMessages(restored), message => message.Text == generated.Text);
        Assert.Contains("AIContextProvider", restoredGenerated.AdditionalProperties![AgentRequestMessageSourceAttribution.AdditionalPropertiesKey]!.ToString(), StringComparison.Ordinal);
    }

    /// <summary>The next model call receives current saved todos after automatic compaction and a todo mutation.</summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task AutomaticCompaction_RestoresCurrentTodosBeforeNextModelCall(bool streaming, bool repeatCompaction, bool removeTodo)
    {
        using var todos = new TodoProvider();
        using var summarizer = new TestChatClient();
        var requests = new List<IReadOnlyList<ChatMessage>>();
        var lookup = AIFunctionFactory.Create(() => "receipt", "lookup");
        ChatMessage Reply(IEnumerable<ChatMessage> messages)
        {
            requests.Add(messages.ToList());
            return new(ChatRole.Assistant, requests.Count switch
            {
                1 when removeTodo => [new FunctionCallContent("complete-1", "todos_remove", new Dictionary<string, object?> { ["ids"] = new[] { 1 } })],
                1 => [new FunctionCallContent("complete-1", "todos_complete", new Dictionary<string, object?>
                {
                    ["items"] = new[] { new { id = 1, reason = "Totals checked" } }
                })],
                2 when repeatCompaction => [new TextContent(new string('y', 8000)), new FunctionCallContent("lookup-1", lookup.Name, new Dictionary<string, object?>())],
                2 => [new FunctionCallContent("lookup-1", lookup.Name, new Dictionary<string, object?>())],
                3 => [new TextContent(new string('z', 8000))],
                _ => [new TextContent("done")]
            });
        }
        using var model = new TestChatClient
        {
            ResponseAsync = (messages, _, _) => Task.FromResult(new ChatResponse(Reply(messages))),
            StreamingAsync = (messages, _, ct) => TestChatClient.StreamAsync(
                [new ChatResponseUpdate { Role = ChatRole.Assistant, MessageId = Guid.NewGuid().ToString(), Contents = Reply(messages).Contents }], ct)
        };
        var reducer = new OscovaSummarizingChatReducer(summarizer, maxTokenCount: 200);
        var agent = await BuildAsync(model, todos, true, [lookup], reducer);
        var session = await agent.CreateSessionAsync();
        var context = await ReadContextAsync(todos, agent, session);
        var add = context.Tools!.OfType<AIFunction>().Single(tool => tool.Name == "todos_add");
        await add.InvokeAsync(new AIFunctionArguments { ["todos"] = new[] { new { title = "Check quote totals", description = "Confirm arithmetic" } } });
        // Reload authoritative state without retaining a creation-tool exchange in history.
        session = await agent.DeserializeSessionAsync(await agent.SerializeSessionAsync(session));
        var input = "Compare these quotes: " + new string('x', 8000);

        if (streaming)
        {
            await foreach (var _ in agent.RunStreamingAsync(input, session)) { }
        }
        else
        {
            await agent.RunAsync(input, session);
        }

        Assert.True(summarizer.ResponseCalls > 0);
        Assert.Equal(3, requests.Count);
        var savedTodos = await todos.GetAllTodosAsync(session);
        if (removeTodo) { Assert.Empty(savedTodos); }
        else { Assert.True(Assert.Single(savedTodos).IsComplete); }
        var current = await ReadContextAsync(todos, agent, session);
        var expected = Assert.Single(current.Messages!).Text;
        Assert.DoesNotContain(requests[0], message => message.Text == expected);
        Assert.Contains(requests[1], message => message.Text == expected);
        Assert.Contains(requests[2], message => message.Text == expected);
        Assert.DoesNotContain(requests[1], message => message.Text == input);
        var contents = requests[1].SelectMany(message => message.Contents).ToList();
        Assert.Single(contents.OfType<FunctionCallContent>(), call => call.CallId == "complete-1");
        Assert.Single(contents.OfType<FunctionResultContent>(), result => result.CallId == "complete-1");
        if (repeatCompaction)
        {
            Assert.Contains(requests[2], message => message.Text == expected && message.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.AIContextProvider);
        }

        // A compaction at the end of one run must not refresh another run or another session twice.
        await agent.RunAsync("Next question", session);
        Assert.Single(requests[3], message => message.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.AIContextProvider);
        var independentSession = await agent.CreateSessionAsync();
        await agent.RunAsync("Independent question", independentSession);
        Assert.Single(requests[4], message => message.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.AIContextProvider);
        if (!removeTodo) { Assert.DoesNotContain(requests[4], message => message.Text == expected); }
    }

    /// <summary>Generated user-role context cannot consume the two submitted-request retention slots.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Compaction_PreservesSubmittedRequestsWithProviderContextAfterReload(bool reload, bool oversizedRecentTurn)
    {
        using var todos = new TodoProvider();
        using var model = new TestChatClient();
        var agent = await BuildAsync(model, todos, false, []);
        var session = await agent.CreateSessionAsync();
        var context = await ReadContextAsync(todos, agent, session);
        var generated = Assert.Single(context.Messages!);
        var recent = new ChatMessage(ChatRole.User, oversizedRecentTurn ? "Recent question" + new string('y', 8000) : "Recent question");
        var latest = new ChatMessage(ChatRole.User, generated.Text);
        var history = Assert.IsType<InMemoryChatHistoryProvider>(agent.ChatHistoryProvider);
        history.SetMessages(session, [new(ChatRole.User, new string('x', 8000)), new(ChatRole.Assistant, "Old answer"),
            recent, generated, new(ChatRole.Assistant, "Recent answer"), latest, generated.Clone()]);
        if (reload) { session = await agent.DeserializeSessionAsync(await agent.SerializeSessionAsync(session)); }
        var reducer = new OscovaSummarizingChatReducer(model, maxTokenCount: 400);
        var storedContext = history.GetMessages(session)[3];
        Assert.False(storedContext.IsSubmittedRequest(), storedContext.AdditionalProperties![AgentRequestMessageSourceAttribution.AdditionalPropertiesKey]!.ToString());

        var reduced = (await reducer.ReduceAsync(history.GetMessages(session), CancellationToken.None)).ToList();

        if (oversizedRecentTurn)
        {
            Assert.DoesNotContain(reduced, message => message.Text == recent.Text);
            Assert.Equal(2, reduced.Count(message => message.Text == generated.Text));
        }
        else
        {
            Assert.Contains(reduced, message => message.Text == recent.Text);
            Assert.Equal(3, reduced.Count(message => message.Text == generated.Text));
            Assert.DoesNotContain("Recent question", Assert.Single(model.ResponseMessages).Single().Text, StringComparison.Ordinal);
        }
    }

    /// <summary>Failed or cancelled reduction cannot cause restoration in a later run or session.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedCompaction_DoesNotScheduleRestoration(bool cancelled)
    {
        using var todos = new TodoProvider();
        using var summarizer = new TestChatClient
        {
            ResponseAsync = (_, _, ct) => Task.FromException<ChatResponse>(cancelled
                ? new OperationCanceledException(ct) : new InvalidOperationException("Summary unavailable"))
        };
        using var model = new TestChatClient();
        var agent = await BuildAsync(model, todos, true, [], new OscovaSummarizingChatReducer(summarizer, maxTokenCount: 200));
        var session = await agent.CreateSessionAsync();

        var failure = await Record.ExceptionAsync(() => agent.RunAsync(new string('x', 8000), session));

        Assert.NotNull(failure);
        Assert.IsType(cancelled ? typeof(OperationCanceledException) : typeof(InvalidOperationException), failure);
        var nextSession = await agent.CreateSessionAsync();
        await agent.RunAsync("Hello", nextSession);
        Assert.Single(model.ResponseMessages[^1], message => message.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.AIContextProvider);
    }

    private static Task<ChatClientAgent> BuildAsync(IChatClient model, TodoProvider todos, bool compaction,
        IList<AITool> tools, IChatReducer? reducer = null)
    {
        var definition = new AgentDefinition { Id = "main", Name = "Main", IsTodoEnabled = true, IsCompactionEnabled = compaction };
        return new AgentOrchestrator().GetOrCreateAgentAsync(new AgentBuildRequest(
            (_, _) => Task.FromResult(model), definition, [definition],
            (_, _) => Task.FromResult<IReadOnlyList<AIContextProvider>>([todos]), _ => [],
            ConfigureOptions: options => options.ChatOptions = new() { Tools = tools },
            ChatReducerFactory: (_, _, _) => Task.FromResult<IChatReducer?>(reducer ?? new OscovaSummarizingChatReducer(model))));
    }

    private static ValueTask<AIContext> ReadContextAsync(TodoProvider todos, AIAgent agent, AgentSession session)
    {
#pragma warning disable MAAI001 // Exercise real provider state and attribution through its invocation contract.
        return todos.InvokingAsync(new(agent, session, new AIContext()));
#pragma warning restore MAAI001
    }
}
