using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Oscova.Agents.Collections;
using Oscova.Agents.Harness;
using Oscova.Agents.Providers;
using Oscova.Agents.Toolsets;

namespace Oscova.Agents.Tests.Framework.Toolsets;

public sealed class WaitToolsetTests
{
    [Fact]
    public async Task WaitAsync_BlankReason_ThrowsToolFailure()
    {
        await Assert.ThrowsAsync<ToolFailureException>(
            () => new WaitToolset().WaitAsync(1, " "));
    }

    [Fact]
    public async Task WaitAsync_WithHandles_WakesForFinishedWorkOrTimesOutWhileItRuns()
    {
        var agent = new ChatClientAgent(new UnusedChatClient(), new ChatClientAgentOptions { Id = "main" });
        var session = await agent.CreateSessionAsync();
        await using var work = new DelegatedWorkState(CancellationToken.None);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        work.Start(session, "held", agent.Id, null, [], (_, _) => Task.CompletedTask,
            (_, ct) => release.Task.WaitAsync(ct));
        work.Watch("job-1", null, job.Task);
        // A later watch, such as a wait resolving a job by ID, never takes the job from its first owner.
        work.Watch("job-1", "other-worker", job.Task);

        var timedOut = await work.WaitAsync(agent, session, ["held", "job-1"], TimeSpan.FromMilliseconds(50), CancellationToken.None);
        Assert.StartsWith("Timed out. Selected work is still pending.\nheld: running\njob-1: running", timedOut, StringComparison.Ordinal);

        var waiting = work.WaitAsync(agent, session, ["held"], TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.False(waiting.IsCompleted);
        release.SetResult("result text");
        Assert.StartsWith("Subagent 'held' completed.\nheld: completed\nresult text", await waiting, StringComparison.Ordinal);

        job.SetResult();
        Assert.StartsWith("Background job 'job-1' finished.",
            await work.WaitAsync(agent, session, ["job-1"], TimeSpan.FromSeconds(30), CancellationToken.None), StringComparison.Ordinal);
        await Assert.ThrowsAsync<ToolFailureException>(() =>
            work.WaitAsync(agent, session, ["unknown"], TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [Fact]
    public async Task ReflectedWait_WhenCancelled_StopsTheDelay()
    {
        var tools = new AgentToolCollection();
        tools.Add(new WaitToolset());
        var qualifiedName = ToolNaming.BuildQualifiedName(
            ToolNaming.GetToolsetOwner(typeof(WaitToolset)), "sleep");
        var tool = Assert.IsAssignableFrom<AIFunction>(tools.Find(qualifiedName));
        Assert.Single(tools);
        using var cancellation = new CancellationTokenSource();
        var arguments = new AIFunctionArguments
        {
            ["durationSeconds"] = 300,
            ["reason"] = "Waiting to recheck an installer."
        };

        ValueTask<object?> invocation = tool.InvokeAsync(arguments, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation.AsTask());
    }

    // Waits only read the agent's services, so the model is never called.
    private sealed class UnusedChatClient : IChatClient
    {
        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
