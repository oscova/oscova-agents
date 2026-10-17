using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Oscova.Agents.Tests.Framework.Conversations;

public sealed class ChatSegmentSerializationTests
{
    [Theory]
    [InlineData(ToolExecutionState.Completed)]
    [InlineData(ToolExecutionState.Failed)]
    public void JsonRoundTrip_PreservesToolExecutionState(ToolExecutionState executionState)
    {
        var result = new FunctionResultContent("call_1", "done");
        if (executionState == ToolExecutionState.Failed)
        {
            result.Exception = new InvalidOperationException("boom");
        }

        var segment = new ChatSegment(
            ChatSegmentType.ToolCall,
            ChatSegmentState.Completed,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            text: null,
            new FunctionCallContent("call_1", "web__search", new Dictionary<string, object?>()),
            result)
        {
            ExecutionState = executionState
        };

        var json = JsonSerializer.Serialize(segment);
        var restored = JsonSerializer.Deserialize<ChatSegment>(json);

        Assert.Contains(nameof(ChatSegment.ExecutionState), json, StringComparison.Ordinal);
        Assert.DoesNotContain("boom", json, StringComparison.Ordinal);
        Assert.NotNull(restored);
        Assert.Equal(executionState, restored.ExecutionState);
    }
}
