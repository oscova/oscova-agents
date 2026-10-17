using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Oscova.Agents.Tasks;
using System.Runtime.CompilerServices;

namespace Oscova.Agents.Tests.Framework.Tasks;

public sealed class AgentTaskRunObserverTests
{
    [Fact]
    public async Task OnRequest_CapturesCurrentUserMessageAndFirstSystemInstruction()
    {
        var changeCount = 0;
        var run = new AgentTaskRun
        {
            LiveMessages = [],
            LiveMessagesChanged = () => changeCount++
        };
        var observer = new AgentTaskRunObserver(run);
        var history = new ChatMessage(ChatRole.User, "Earlier input")
            .WithAgentRequestMessageSource(AgentRequestMessageSourceType.ChatHistory);
        var current = new ChatMessage(ChatRole.User, "Current input");

        await observer.OnRequestAsync([history, current], new ChatOptions { Instructions = "First instruction" });
        await observer.OnRequestAsync(
            [new ChatMessage(ChatRole.User, "Later input")],
            new ChatOptions { Instructions = "Later instruction" });

        var secondRun = new AgentTaskRun { LiveMessages = [] };
        var secondObserver = new AgentTaskRunObserver(secondRun);
        await secondObserver.OnRequestAsync([current], options: null);

        var captured = Assert.Single(run.LiveMessages!);
        Assert.Same(current, captured);
        Assert.Equal("First instruction", run.SystemInstruction);
        Assert.Equal(1, changeCount);
        Assert.Same(current, Assert.Single(secondRun.LiveMessages!));
    }

    [Fact]
    public async Task OnRequest_AppendsOnlyEligibleResultMessages()
    {
        var changeCount = 0;
        var run = new AgentTaskRun
        {
            LiveMessages = [],
            LiveMessagesChanged = () => changeCount++
        };
        var observer = new AgentTaskRunObserver(run);
        var historyResult = new ChatMessage(
                ChatRole.Tool,
                [new FunctionResultContent("call-history", "history")])
            .WithAgentRequestMessageSource(AgentRequestMessageSourceType.ChatHistory);
        var currentAfterHistory = new ChatMessage(
            ChatRole.Tool,
            [new FunctionResultContent("call-history", "current")]);
        var named = new ChatMessage(
            ChatRole.Tool,
            [new FunctionResultContent("call-1", "named")]);
        // The current dependency constructor rejects null call IDs, while the moved
        // observer deliberately retains its existing defensive null branch.
        var namelessResult = (FunctionResultContent)RuntimeHelpers.GetUninitializedObject(
            typeof(FunctionResultContent));
        namelessResult.Result = "nameless";
        var nameless = new ChatMessage(ChatRole.Tool, [namelessResult]);

        await observer.OnRequestAsync([historyResult], options: null);
        await observer.OnRequestAsync([currentAfterHistory], options: null);
        await observer.OnRequestAsync([named], options: null);
        await observer.OnRequestAsync([named], options: null);
        await observer.OnRequestAsync([nameless], options: null);
        await observer.OnRequestAsync([nameless], options: null);

        var secondRun = new AgentTaskRun { LiveMessages = [] };
        var secondObserver = new AgentTaskRunObserver(secondRun);
        await secondObserver.OnRequestAsync([named], options: null);

        Assert.Equal([currentAfterHistory, named, nameless, nameless], run.LiveMessages);
        Assert.Equal(4, changeCount);
        Assert.Same(named, Assert.Single(secondRun.LiveMessages!));
    }

    [Fact]
    public void OnResponse_AppendsNonStreamingResponseAndSignalsChange()
    {
        var changeCount = 0;
        var run = new AgentTaskRun
        {
            LiveMessages = [],
            LiveMessagesChanged = () => changeCount++
        };
        var observer = new AgentTaskRunObserver(run);
        var message = new ChatMessage(ChatRole.Assistant, "Done");

        observer.OnResponse(new ChatResponse(message));

        Assert.Same(message, Assert.Single(run.LiveMessages!));
        Assert.Equal(1, changeCount);
    }
}
