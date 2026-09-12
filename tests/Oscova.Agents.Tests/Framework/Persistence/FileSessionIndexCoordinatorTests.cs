using Oscova.Agents.Persistence;

namespace Oscova.Agents.Tests.Framework.Persistence;

public sealed class FileSessionIndexCoordinatorTests
{
    [Fact]
    public async Task RunForUserAsync_SameKey_SerializesUpdates()
    {
        var coordinator = new FileSessionIndexCoordinator();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = coordinator.RunForUserAsync("user-1", async ct =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task.WaitAsync(ct);
        });
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = coordinator.RunForUserAsync("user-1", _ =>
        {
            secondEntered.SetResult();
            return Task.CompletedTask;
        });

        Assert.False(secondEntered.Task.IsCompleted);

        releaseFirst.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(secondEntered.Task.IsCompleted);
    }

    [Fact]
    public async Task RunForTaskAsync_SeparatorBearingDistinctKeys_DoNotCollide()
    {
        var coordinator = new FileSessionIndexCoordinator();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = coordinator.RunForTaskAsync("a:b", "c", async ct =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task.WaitAsync(ct);
        });
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = coordinator.RunForTaskAsync("a", "b:c", _ =>
        {
            secondEntered.SetResult();
            return Task.CompletedTask;
        });

        try
        {
            await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            releaseFirst.SetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
