using Microsoft.Extensions.AI;
using Oscova.Agents.Middleware;

namespace Oscova.Agents.Tests.Framework.Middleware;

public sealed class GovernedFunctionInvokingChatClientTests
{
    /// <summary>Only a tool loop cut off at its iteration limit is reported as stopped by the limit.</summary>
    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public async Task GetResponseAsync_ReportsIterationLimitOnlyWhenToolLoopIsCutOff(int toolRounds, bool expected)
    {
        // Arrange: a model that asks for the tool a fixed number of times, then answers.
        var tool = AIFunctionFactory.Create(() => "pong", "ping");
        var calls = 0;
        using var transport = new TestChatClient
        {
            ResponseAsync = (_, _, _) => Task.FromResult(++calls <= toolRounds
                ? new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"call-{calls}", tool.Name)]))
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, "Finished")))
        };
        using var client = new GovernedFunctionInvokingChatClient(transport, null, null) { MaximumIterationsPerRequest = 3 };

        // Act
        var response = await client.GetResponseAsync([new(ChatRole.User, "Run")], new ChatOptions { Tools = [tool] });

        // Assert
        Assert.Equal(expected, GovernedFunctionInvokingChatClient.ReachedIterationLimit(response.AdditionalProperties));
    }

    /// <summary>An approved call resumed in a later request that then finishes is not reported as stopped by the limit.</summary>
    [Fact]
    public async Task GetResponseAsync_ApprovalResumeThatFinishes_DoesNotReportIterationLimit()
    {
        // Arrange: the first request ends with an approval request for the tool.
        var invocations = 0;
        var tool = new ApprovalRequiredAIFunction(AIFunctionFactory.Create(() => ++invocations, "ping"));
        var calls = 0;
        using var transport = new TestChatClient
        {
            ResponseAsync = (_, _, _) => Task.FromResult(++calls == 1
                ? new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", tool.Name)]))
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, "Finished")))
        };
        using var client = new GovernedFunctionInvokingChatClient(transport, null, null) { MaximumIterationsPerRequest = 1 };
        var options = new ChatOptions { Tools = [tool] };
        List<ChatMessage> history = [new(ChatRole.User, "Run")];
        var first = await client.GetResponseAsync(history, options);
        var request = Assert.Single(first.Messages.SelectMany(message => message.Contents).OfType<ToolApprovalRequestContent>());
        history.AddRange(first.Messages);
        history.Add(new ChatMessage(ChatRole.User, [request.CreateResponse(approved: true)]));

        // Act: the approval resumes the call, then the model answers.
        var resumed = await client.GetResponseAsync(history, options);

        // Assert: the approved call ran and the finished response is not reported as cut off.
        Assert.Equal(1, invocations);
        Assert.Equal("Finished", resumed.Text);
        Assert.False(GovernedFunctionInvokingChatClient.ReachedIterationLimit(resumed.AdditionalProperties));
    }
}
