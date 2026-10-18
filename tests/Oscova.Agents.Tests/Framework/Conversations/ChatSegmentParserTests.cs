using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Oscova.Agents.Tests.Framework.Conversations;

public sealed class ChatSegmentParserTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ToolObservers_UpdateTheMatchingInvocation(bool detached, bool failed)
    {
        var parser = new ChatSegmentParser();
        var first = new FunctionCallContent("first", "search", new Dictionary<string, object?>());
        var second = new FunctionCallContent("second", "search", new Dictionary<string, object?>());
        parser.StartToolCall(first);
        parser.StartToolCall(second);
        var firstSegment = parser.Segments[0];
        var secondSegment = parser.Segments[1];

        parser.OnToolStarted(first);
        Assert.Equal(ToolExecutionState.Executing, firstSegment.ExecutionState);
        Assert.Equal(ToolExecutionState.Pending, secondSegment.ExecutionState);
        parser.OnToolStarted(second);
        if (detached) { parser.Reset(); }
        if (failed) { parser.OnToolFailed(first); }
        else { parser.OnToolCompleted(first); }

        Assert.Equal(failed ? ToolExecutionState.Failed : ToolExecutionState.Completed, firstSegment.ExecutionState);
        Assert.Equal(ToolExecutionState.Executing, secondSegment.ExecutionState);
        parser.Process(new AgentResponseUpdate { Contents = [new FunctionResultContent("first", "result")] });
        Assert.Equal("result", firstSegment.FunctionResult?.Result);
        Assert.Equal(failed ? ToolExecutionState.Failed : ToolExecutionState.Completed, firstSegment.ExecutionState);
    }
}
