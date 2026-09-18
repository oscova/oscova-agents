namespace Oscova.Agents.Tests.Framework.Conversations;

public sealed class ThreadRunGuardTests
{
    [Fact]
    public void GetActiveRunId_ReturnsRunIdUntilGuardIsReleased()
    {
        var guard = new ThreadRunGuard();
        const string userId = "user-1";
        const string threadId = "thread-1";
        const string runId = "run-1";

        var lease = guard.Acquire(userId, threadId, runId);

        Assert.NotNull(lease);
        Assert.Equal(runId, guard.GetActiveRunId(userId, threadId));

        lease!.Dispose();
        Assert.Null(guard.GetActiveRunId(userId, threadId));
    }

    [Fact]
    public void Cancel_FiresLeaseTokenAndReturnsRunId()
    {
        var guard = new ThreadRunGuard();
        const string userId = "user-1";
        const string threadId = "thread-1";
        const string runId = "run-1";

        using var lease = guard.Acquire(userId, threadId, runId);

        Assert.NotNull(lease);
        Assert.False(lease.CancellationToken.IsCancellationRequested);

        var cancelledRunId = guard.Cancel(userId, threadId);

        Assert.Equal(runId, cancelledRunId);
        Assert.True(lease.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void Cancel_WhenNoRunActive_ReturnsNull()
    {
        var guard = new ThreadRunGuard();

        Assert.Null(guard.Cancel("user-1", "thread-1"));
    }

    [Fact]
    public void CancelOrDefer_BeforeAcquire_CancelsOnlyMatchingRun()
    {
        var guard = new ThreadRunGuard();

        guard.CancelOrDefer("user-1", "thread-1", "run-target");

        using (var otherLease = guard.Acquire("user-1", "thread-1", "run-other"))
        {
            Assert.NotNull(otherLease);
            Assert.False(otherLease.CancellationToken.IsCancellationRequested);
        }

        using var targetLease = guard.Acquire("user-1", "thread-1", "run-target");
        Assert.NotNull(targetLease);
        Assert.True(targetLease.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void CancelOrDefer_WithStaleRunId_DoesNotCancelDifferentActiveRun()
    {
        var guard = new ThreadRunGuard();
        using var activeLease = guard.Acquire("user-1", "thread-1", "run-current");

        guard.CancelOrDefer("user-1", "thread-1", "run-stale");

        Assert.NotNull(activeLease);
        Assert.False(activeLease.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void CancelOrDefer_WhileAnotherRunOwnsThread_CancelsTargetAfterAdmission()
    {
        var guard = new ThreadRunGuard();
        var ownerLease = guard.Acquire("user-1", "thread-1", "run-owner");
        Assert.NotNull(ownerLease);

        guard.CancelOrDefer("user-1", "thread-1", "run-target");

        Assert.False(ownerLease.CancellationToken.IsCancellationRequested);
        ownerLease.Dispose();

        using var targetLease = guard.Acquire("user-1", "thread-1", "run-target");
        Assert.NotNull(targetLease);
        Assert.True(targetLease.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void CancelExact_ForReleasedRun_DoesNotCancelReplacementLease()
    {
        var guard = new ThreadRunGuard();
        var releasedLease = guard.Acquire("user-1", "thread-1", "run-old");
        Assert.NotNull(releasedLease);
        releasedLease.Dispose();

        using var replacementLease = guard.Acquire("user-1", "thread-1", "run-new");
        var cancelled = guard.CancelExact("user-1", "thread-1", "run-old");

        Assert.False(cancelled);
        Assert.NotNull(replacementLease);
        Assert.False(replacementLease.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void CaseVariant_AddressesSameThreadRun()
    {
        var guard = new ThreadRunGuard();
        using var activeLease = guard.Acquire("user-1", "thread-ab12", "run-1");
        Assert.NotNull(activeLease);

        using var variantLease = guard.Acquire("user-1", "THREAD-AB12", "run-2");
        using var distinctUserLease = guard.Acquire("USER-1", "THREAD-AB12", "run-3");
        var visibleRunId = guard.GetActiveRunId("user-1", "THREAD-AB12");
        var cancelledRunId = guard.Cancel("user-1", "THREAD-AB12");

        Assert.Null(variantLease);
        Assert.NotNull(distinctUserLease);
        Assert.Equal("run-1", visibleRunId);
        Assert.Equal("run-1", cancelledRunId);
        Assert.True(activeLease.CancellationToken.IsCancellationRequested);

        activeLease.Dispose();
        using var replacementLease = guard.Acquire("user-1", "THREAD-AB12", "run-4");
        Assert.NotNull(replacementLease);
    }

    [Fact]
    public async Task AcquireAsync_WaitsUntilExistingLeaseIsReleased()
    {
        var guard = new ThreadRunGuard();
        var activeLease = guard.Acquire("user-1", "thread-1", "run-1");
        Assert.NotNull(activeLease);

        var waitingLease = guard.AcquireAsync("user-1", "thread-1", "run-2");
        Assert.False(waitingLease.IsCompleted);

        activeLease.Dispose();
        using var acquiredLease = await waitingLease.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("run-2", guard.GetActiveRunId("user-1", "thread-1"));
    }

    [Fact]
    public async Task CancelAndAcquireUserAsync_DrainsAndBlocksOnlyExactUser()
    {
        var guard = new ThreadRunGuard();
        var firstLease = guard.Acquire("user-1", "thread-1", "run-1");
        var secondLease = guard.Acquire("user-1", "thread-2", "run-2");
        using var distinctUserLease = guard.Acquire("USER-1", "thread-1", "run-3");
        Assert.NotNull(firstLease);
        Assert.NotNull(secondLease);
        Assert.NotNull(distinctUserLease);

        var userLeaseTask = guard.CancelAndAcquireUserAsync("user-1");

        Assert.True(firstLease!.CancellationToken.IsCancellationRequested);
        Assert.True(secondLease!.CancellationToken.IsCancellationRequested);
        Assert.False(distinctUserLease.CancellationToken.IsCancellationRequested);
        Assert.False(userLeaseTask.IsCompleted);

        firstLease.Dispose();
        secondLease.Dispose();
        using var userLease = await userLeaseTask.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Null(guard.Acquire("user-1", "thread-3", "run-4"));
        using var exactDistinctUserLease = guard.Acquire("USER-1", "thread-3", "run-5");
        Assert.NotNull(exactDistinctUserLease);

        userLease.Dispose();
        using var replacementLease = guard.Acquire("user-1", "thread-3", "run-6");
        Assert.NotNull(replacementLease);
    }

    [Fact]
    public async Task CancelAndAcquireUserAsync_WithThreadSelection_CancelsSelectedAndDrainsAll()
    {
        var guard = new ThreadRunGuard();
        var selectedLease = guard.Acquire("user-1", "thread-1", "run-1");
        var unselectedLease = guard.Acquire("user-1", "thread-2", "run-2");
        Assert.NotNull(selectedLease);
        Assert.NotNull(unselectedLease);

        var userLeaseTask = guard.CancelAndAcquireUserAsync(
            "user-1",
            threadIdsToCancel: ["THREAD-1"]);

        Assert.True(selectedLease!.CancellationToken.IsCancellationRequested);
        Assert.False(unselectedLease!.CancellationToken.IsCancellationRequested);
        Assert.False(userLeaseTask.IsCompleted);

        selectedLease.Dispose();
        Assert.False(userLeaseTask.IsCompleted);

        unselectedLease.Dispose();
        using var userLease = await userLeaseTask.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Null(guard.Acquire("user-1", "thread-3", "run-3"));

        userLease.Dispose();
        using var replacementLease = guard.Acquire("user-1", "thread-3", "run-4");
        Assert.NotNull(replacementLease);
    }
}
