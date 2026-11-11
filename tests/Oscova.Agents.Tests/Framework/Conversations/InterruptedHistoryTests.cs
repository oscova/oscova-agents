using Microsoft.Extensions.AI;

namespace Oscova.Agents.Tests.Framework.Conversations;

/// <summary>Protects resumable native tool history without altering its running source.</summary>
public sealed class InterruptedHistoryTests
{
    /// <summary>Unfinished exchanges are omitted while text and completed exchanges survive in a separate copy.</summary>
    [Fact]
    public void RemoveUnfinishedToolExchanges_PreservesCompletedContentAndSource()
    {
        var source = new List<ChatMessage>
        {
            new(ChatRole.User, "Question"),
            new(ChatRole.Assistant, [new TextContent("Working"), new FunctionCallContent("done", "read"),
                new FunctionCallContent("pending", "notify")]),
            new(ChatRole.Tool, [new FunctionResultContent("done", "Result"), new FunctionResultContent("orphan", "Ignored")])
        };

        var result = InterruptedHistory.RemoveUnfinishedToolExchanges(source);

        Assert.Equal("Question", result[0].Text);
        Assert.Equal("Working", result[1].Text);
        Assert.Equal("done", Assert.Single(result[1].Contents.OfType<FunctionCallContent>()).CallId);
        Assert.Equal("done", Assert.Single(result[2].Contents.OfType<FunctionResultContent>()).CallId);
        Assert.Equal(2, source[1].Contents.OfType<FunctionCallContent>().Count());
        Assert.Equal(2, source[2].Contents.OfType<FunctionResultContent>().Count());
    }
}
