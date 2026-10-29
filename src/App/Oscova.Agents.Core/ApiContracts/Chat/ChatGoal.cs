namespace Oscova.Agents.Core.ApiContracts.Chat;

/// <summary>Identifies the continuation state shown above the conversation composer.</summary>
public enum ChatGoalStatus { Active, Paused, Blocked, Achieved }

/// <summary>Stores the objective and continuation allowance independently of chat history.</summary>
public sealed record ChatGoal
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Objective { get; init; } = string.Empty;
    public int Version { get; init; } = 1;
    public ChatGoalStatus Status { get; init; } = ChatGoalStatus.Active;
    public string? Reason { get; init; }
    public string? AgentId { get; init; }
    public string? ModelProviderId { get; init; }
    public string? RunId { get; init; }
    public int Iterations { get; init; }
    public int MaxIterations { get; init; } = int.MaxValue;
    public long ElapsedMilliseconds { get; init; }
    public DateTimeOffset? ActiveSince { get; init; }
    // Queue handoff retains the final response until ordinary work releases the conversation.
    public string? PendingResponse { get; init; }
    public string? PendingSteering { get; init; }

    /// <summary>Checkpoints active time without counting a later process restart as work.</summary>
    public ChatGoal WithClockCheckpoint() => this with
    {
        ElapsedMilliseconds = ElapsedMilliseconds + (ActiveSince is { } start
            ? Math.Max(0, (long)(DateTimeOffset.UtcNow - start).TotalMilliseconds) : 0),
        ActiveSince = DateTimeOffset.UtcNow
    };

    /// <summary>Stops the elapsed clock when continuation pauses or finishes.</summary>
    public ChatGoal WithStatus(ChatGoalStatus status, string? reason = null) => WithClockCheckpoint() with
    {
        Status = status, Reason = reason,
        ActiveSince = null, RunId = null,
        PendingResponse = status == ChatGoalStatus.Active ? PendingResponse : null,
        PendingSteering = status == ChatGoalStatus.Active ? PendingSteering : null
    };
}

/// <summary>Captures an objective without creating a human chat message.</summary>
public sealed record SaveChatGoalRequest(string Objective, string? AgentId = null, string? ModelProviderId = null);
