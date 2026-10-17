using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using OscovaSummarizingChatReducer = Oscova.Agents.Reducers.SummarizingChatReducer;

namespace Oscova.Agents.Tests.Framework.Reducers;

public sealed class SummarizingChatReducerTests
{
    /// <summary>Consecutive streamed tool results retain their calls through per-service-call compaction.</summary>
    [Theory]
    [InlineData("first", "second")]
    [InlineData("reused", "reused")]
    [InlineData("", "")]
    public async Task StreamingRun_WhenCompactionOccursBetweenCallAndResult_PreservesThePair(string firstCallId, string secondCallId)
    {
        var tool = AIFunctionFactory.Create((string payload) => "receipt", "lookup");
        var providerCalls = 0;
        using var model = new TestChatClient
        {
            StreamingAsync = (messages, _, cancellationToken) =>
            {
                providerCalls++;
                var contents = messages.SelectMany(message => message.Contents).ToList();
                if (providerCalls > 1)
                {
                    var result = Assert.Single(contents.OfType<FunctionResultContent>());
                    var call = Assert.Single(contents.OfType<FunctionCallContent>(), call => call.CallId == result.CallId);
                    Assert.Equal(providerCalls == 2 ? firstCallId : secondCallId, call.CallId);
                    Assert.Equal(tool.Name, call.Name);
                    Assert.Equal("receipt", result.Result?.ToString());
                }
                return TestChatClient.StreamAsync([new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant, MessageId = providerCalls.ToString(),
                    Contents = providerCalls <= 2
                        ? [new FunctionCallContent(providerCalls == 1 ? firstCallId : secondCallId, tool.Name,
                            new Dictionary<string, object?> { ["payload"] = new string('x', 8000) })]
                        : [new TextContent("completed")]
                }], cancellationToken);
            }
        };
        using var summarizer = new TestChatClient();
        var history = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
        {
            ChatReducer = new OscovaSummarizingChatReducer(summarizer, maxTokenCount: 100),
            ReducerTriggerEvent = InMemoryChatHistoryProviderOptions.ChatReducerTriggerEvent.AfterMessageAdded
        });
        var agent = new ChatClientAgent(model, new ChatClientAgentOptions
        {
            Name = "Assistant", ChatHistoryProvider = history, RequirePerServiceCallChatHistoryPersistence = true,
            ChatOptions = new ChatOptions { Tools = [tool] }
        });
        var reply = string.Empty;
        await foreach (var update in agent.RunStreamingAsync("Run the lookup")) { reply += update.Text; }
        Assert.Equal("completed", reply);
        Assert.Equal(3, providerCalls);
        Assert.True(summarizer.ResponseCalls > 0);
    }

    /// <summary>Compaction retains an outstanding call until its real result can be paired with it.</summary>
    [Theory]
    [InlineData("pending-call")]
    [InlineData("")]
    public async Task ReduceAsync_WhenCurrentTurnExceedsBudget_PreservesOutstandingToolCall(string callId)
    {
        var inner = new TestChatClient();
        var reducer = new OscovaSummarizingChatReducer(inner, maxTokenCount: 100);
        var toolCall = new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent(callId, "lookup", new Dictionary<string, object?>())]);
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "Find the saved conversation"),
            new(ChatRole.Assistant, [new FunctionCallContent(callId, "lookup", new Dictionary<string, object?>())]),
            new(ChatRole.Tool, [new FunctionResultContent(callId, new string('x', 8000))]),
            toolCall
        };

        var reduced = (await reducer.ReduceAsync(history, CancellationToken.None)).ToList();

        Assert.Contains(reduced, message => ReferenceEquals(message, toolCall));
        Assert.Equal(callId, reduced.SelectMany(message => message.Contents).OfType<FunctionCallContent>().Last().CallId);
    }

    [Fact]
    public async Task ReduceAsync_WhenUnderThreshold_ReturnsHistoryUnchanged()
    {
        var inner = new TestChatClient();
        var reducer = new OscovaSummarizingChatReducer(inner);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system"),
            new(ChatRole.User, "hello"),
            new(ChatRole.Assistant, "hi")
        };

        var reduced = (await reducer.ReduceAsync(messages, CancellationToken.None)).ToList();

        Assert.Equal(messages.Count, reduced.Count);
        Assert.All(Enumerable.Range(0, messages.Count), index => Assert.Same(messages[index], reduced[index]));
        Assert.Equal(0, inner.ResponseCalls);
    }

    [Fact]
    public async Task ReduceAsync_WhenToolPayloadExceedsThreshold_CompactsEvenWhenTextIsSmall()
    {
        var inner = new TestChatClient();
        var reducer = new OscovaSummarizingChatReducer(inner, maxTokenCount: 100, compactionThreshold: 0.8);
        var messages = CreateToolHeavyHistory(new string('a', 8000));

        var reduced = (await reducer.ReduceAsync(messages, CancellationToken.None)).ToList();

        Assert.Contains(reduced, message => message.Text.Contains("[Tool calls omitted: search]", StringComparison.Ordinal));
        Assert.DoesNotContain(reduced, message => message.Role == ChatRole.Tool);
        Assert.Equal(0, inner.ResponseCalls);
    }

    [Fact]
    public async Task ReduceAsync_WhenStrippingFitsBudget_RemovesOldToolRoundTripsWithoutModelCall()
    {
        var inner = new TestChatClient();
        var reducer = new OscovaSummarizingChatReducer(inner, maxTokenCount: 120, compactionThreshold: 0.8);
        var system = new ChatMessage(ChatRole.System, "system");
        var recentUser = new ChatMessage(ChatRole.User, "recent question");
        var recentAssistant = new ChatMessage(ChatRole.Assistant, "recent answer");
        var latestUser = new ChatMessage(ChatRole.User, "latest question");
        var latestAssistant = new ChatMessage(ChatRole.Assistant, "latest answer");
        var messages = CreateToolHeavyHistory(
            new string('r', 9000),
            system,
            recentUser,
            recentAssistant,
            latestUser,
            latestAssistant);

        var reduced = (await reducer.ReduceAsync(messages, CancellationToken.None)).ToList();

        Assert.Same(system, reduced[0]);
        Assert.Contains(reduced, message => message.Text.Contains("[Tool calls omitted: search]", StringComparison.Ordinal));
        Assert.DoesNotContain(reduced, message => message.Contents.OfType<FunctionCallContent>().Any());
        Assert.DoesNotContain(reduced, message => message.Role == ChatRole.Tool);
        AssertEndsWith([recentUser, recentAssistant, latestUser, latestAssistant], reduced);
        Assert.Equal(0, inner.ResponseCalls);
    }

    [Fact]
    public async Task ReduceAsync_WhenStrippedHistoryExceedsThresholdButFitsBudget_DoesNotSummarize()
    {
        var inner = new TestChatClient();
        var reducer = new OscovaSummarizingChatReducer(inner, maxTokenCount: 2000, compactionThreshold: 0.01);
        var messages = CreateToolHeavyHistory(new string('s', 9000));

        var reduced = (await reducer.ReduceAsync(messages, CancellationToken.None)).ToList();

        Assert.Contains(reduced, message => message.Text.Contains("[Tool calls omitted: search]", StringComparison.Ordinal));
        Assert.DoesNotContain(reduced, message => message.Text.StartsWith("[Conversation Summary]", StringComparison.Ordinal));
        Assert.Equal(0, inner.ResponseCalls);
    }

    [Fact]
    public async Task ReduceAsync_WhenStrippingDoesNotFit_SummarizesMiddleAndPreservesTwoUserTurns()
    {
        var inner = new TestChatClient
        {
            ResponseAsync = (_, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "summary text")))
        };
        var reducer = new OscovaSummarizingChatReducer(inner, maxTokenCount: 100, compactionThreshold: 0.8);
        var system = new ChatMessage(ChatRole.System, "system");
        var recentUser = new ChatMessage(ChatRole.User, "recent question");
        var recentAssistant = new ChatMessage(ChatRole.Assistant, "recent answer");
        var latestUser = new ChatMessage(ChatRole.User, "latest question");
        var latestAssistant = new ChatMessage(ChatRole.Assistant, "latest answer");
        var oldText = new string('m', 5000);
        var messages = new List<ChatMessage>
        {
            system,
            new(ChatRole.User, oldText),
            new(ChatRole.Assistant, oldText),
            recentUser,
            recentAssistant,
            latestUser,
            latestAssistant
        };

        var reduced = (await reducer.ReduceAsync(messages, CancellationToken.None)).ToList();

        Assert.Equal(1, inner.ResponseCalls);
        Assert.Same(system, reduced[0]);
        Assert.Equal("[Conversation Summary]\nsummary text", reduced[1].Text);
        AssertEndsWith([recentUser, recentAssistant, latestUser, latestAssistant], reduced);

        var prompt = Assert.Single(inner.ResponseMessages).Single().Text;
        Assert.Contains(oldText, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("recent question", prompt, StringComparison.Ordinal);
        var options = Assert.Single(inner.ResponseOptions);
        Assert.NotNull(options?.AdditionalProperties);
        Assert.False((bool)options.AdditionalProperties["think"]!);
    }

    [Fact]
    public async Task ReduceAsync_WhenTailBoundaryTouchesToolSequence_MovesBoundaryBeforeSequence()
    {
        var inner = new TestChatClient
        {
            ResponseAsync = (_, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "summary")))
        };
        var reducer = new OscovaSummarizingChatReducer(inner, maxTokenCount: 200, compactionThreshold: 0.8);
        var call = new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?> { ["query"] = "x" });
        var toolCall = new ChatMessage(ChatRole.Assistant, [call]);
        var toolResult = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "result")]);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system"),
            new(ChatRole.User, new string('o', 5000)),
            toolCall,
            toolResult,
            new(ChatRole.User, "recent question"),
            new(ChatRole.Assistant, "recent answer"),
            new(ChatRole.User, "latest question"),
            new(ChatRole.Assistant, "latest answer")
        };

        var reduced = (await reducer.ReduceAsync(messages, CancellationToken.None)).ToList();

        Assert.Contains(reduced, message => ReferenceEquals(message, toolCall));
        Assert.Contains(reduced, message => ReferenceEquals(message, toolResult));
        Assert.Equal(1, inner.ResponseCalls);
    }

    [Fact]
    public async Task ReduceAsync_AwaitsOnBeforeCompactionBeforeStripOnlyCompaction()
    {
        var inner = new TestChatClient();
        var reducer = new OscovaSummarizingChatReducer(inner, maxTokenCount: 100, compactionThreshold: 0.8);
        var beforeCalled = false;
        var startedAfterBefore = false;
        reducer.OnBeforeCompaction = async _ =>
        {
            await Task.Yield();
            beforeCalled = true;
        };
        reducer.OnCompactionStarted = () => startedAfterBefore = beforeCalled;

        await reducer.ReduceAsync(CreateToolHeavyHistory(new string('a', 8000)), CancellationToken.None);

        Assert.True(beforeCalled);
        Assert.True(startedAfterBefore);
        Assert.Equal(0, inner.ResponseCalls);
    }

    [Fact]
    public async Task ReduceAsync_AwaitsOnBeforeCompactionBeforeSummarization()
    {
        var beforeCalled = false;
        var inner = new TestChatClient
        {
            ResponseAsync = (_, _, _) =>
            {
                Assert.True(beforeCalled);
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "summary")));
            }
        };
        var reducer = new OscovaSummarizingChatReducer(inner, maxTokenCount: 100, compactionThreshold: 0.8)
        {
            OnBeforeCompaction = async _ =>
            {
                await Task.Yield();
                beforeCalled = true;
            }
        };
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, new string('g', 5000)),
            new(ChatRole.Assistant, new string('h', 5000)),
            new(ChatRole.User, "recent"),
            new(ChatRole.Assistant, "response"),
            new(ChatRole.User, "latest")
        };

        await reducer.ReduceAsync(messages, CancellationToken.None);

        Assert.True(beforeCalled);
        Assert.Equal(1, inner.ResponseCalls);
    }

    [Fact]
    public async Task ReduceAsync_WhenSingleTurnIsTooLarge_FallsBackToWholeHistorySummary()
    {
        var inner = new TestChatClient
        {
            ResponseAsync = (_, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "tiny summary")))
        };
        var reducer = new OscovaSummarizingChatReducer(inner, maxTokenCount: 50, compactionThreshold: 0.8);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, new string('z', 10000))
        };

        var reduced = (await reducer.ReduceAsync(messages, CancellationToken.None)).ToList();

        var summary = Assert.Single(reduced);
        Assert.Equal("[Conversation Summary]\ntiny summary", summary.Text);
        Assert.Equal(1, inner.ResponseCalls);
    }

    [Fact]
    public async Task CompactAsync_BelowAutomaticThreshold_CompactsOlderHistory()
    {
        var inner = new TestChatClient
        {
            ResponseAsync = (_, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "short")))
        };
        var reducer = new OscovaSummarizingChatReducer(inner);
        var recentUser = new ChatMessage(ChatRole.User, "recent");
        var recentAssistant = new ChatMessage(ChatRole.Assistant, "recent answer");
        var latestUser = new ChatMessage(ChatRole.User, "latest");
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "old"),
            new(ChatRole.Assistant, "old answer"),
            recentUser,
            recentAssistant,
            latestUser
        };

        var compacted = (await reducer.CompactAsync(messages)).ToList();

        Assert.Equal(1, inner.ResponseCalls);
        AssertEndsWith([recentUser, recentAssistant, latestUser], compacted);
    }

    [Fact]
    public async Task CompactAsync_WithOnlyProtectedTail_ReturnsOriginalReferencesWithoutCallbacks()
    {
        var inner = new TestChatClient();
        var callbacks = 0;
        var reducer = new OscovaSummarizingChatReducer(inner)
        {
            OnBeforeCompaction = _ =>
            {
                callbacks++;
                return Task.CompletedTask;
            },
            OnCompactionStarted = () => callbacks++,
            OnCompactionCompleted = () => callbacks++
        };
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "recent"),
            new(ChatRole.Assistant, "answer"),
            new(ChatRole.User, "latest")
        };

        var compacted = (await reducer.CompactAsync(messages)).ToList();

        Assert.All(Enumerable.Range(0, messages.Count), index => Assert.Same(messages[index], compacted[index]));
        Assert.Equal(0, callbacks);
        Assert.Equal(0, inner.ResponseCalls);
    }

    [Fact]
    public async Task CompactAsync_WhenRepeated_RunsTheNormalForcedPathAgain()
    {
        var inner = new TestChatClient
        {
            ResponseAsync = (_, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "short")))
        };
        var reducer = new OscovaSummarizingChatReducer(inner);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "old"),
            new(ChatRole.Assistant, "old answer"),
            new(ChatRole.User, "recent"),
            new(ChatRole.Assistant, "recent answer"),
            new(ChatRole.User, "latest")
        };

        var first = await reducer.CompactAsync(messages);
        await reducer.CompactAsync(first);

        Assert.Equal(2, inner.ResponseCalls);
    }

    private static List<ChatMessage> CreateToolHeavyHistory(
        string payload,
        ChatMessage? system = null,
        ChatMessage? recentUser = null,
        ChatMessage? recentAssistant = null,
        ChatMessage? latestUser = null,
        ChatMessage? latestAssistant = null)
    {
        return
        [
            system ?? new ChatMessage(ChatRole.System, "system"),
            new(ChatRole.User, "old question"),
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "search", new Dictionary<string, object?> { ["query"] = payload })]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", payload)]),
            new(ChatRole.Assistant, "old answer"),
            recentUser ?? new ChatMessage(ChatRole.User, "recent question"),
            recentAssistant ?? new ChatMessage(ChatRole.Assistant, "recent answer"),
            latestUser ?? new ChatMessage(ChatRole.User, "latest question"),
            latestAssistant ?? new ChatMessage(ChatRole.Assistant, "latest answer")
        ];
    }

    private static void AssertEndsWith(IReadOnlyList<ChatMessage> expectedTail, IReadOnlyList<ChatMessage> actual)
    {
        Assert.True(actual.Count >= expectedTail.Count);
        var offset = actual.Count - expectedTail.Count;
        for (var i = 0; i < expectedTail.Count; i++)
        {
            Assert.Same(expectedTail[i], actual[offset + i]);
        }
    }
}
