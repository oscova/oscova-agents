using Microsoft.Extensions.AI;
using Oscova.Agents.Collections;
using Oscova.Agents.Harness;
using Oscova.Agents.Toolsets;

namespace Oscova.Agents.Tests.Framework.Toolsets;

public sealed class WaitToolsetTests
{
    [Fact]
    public async Task WaitAsync_BlankReason_ThrowsToolFailure()
    {
        await Assert.ThrowsAsync<ToolFailureException>(
            () => WaitToolset.WaitAsync(1, " "));
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
}
