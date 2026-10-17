namespace Oscova.Agents.Conversations;

/// <summary>Records how an approval-required tool call was resolved for transcript display.</summary>
public enum ToolApprovalOutcome
{
    /// <summary>The user approved one call.</summary>
    AllowedOnce,
    /// <summary>The user approved this tool for the server session.</summary>
    AllowedForSession,
    /// <summary>The user denied this call.</summary>
    Denied,
    /// <summary>A matching temporary grant approved the call.</summary>
    SessionPermission,
    /// <summary>The admitted run automatically approved the call.</summary>
    AutoApproved,
    /// <summary>The admitted run automatically rejected the call.</summary>
    AutoRejected,
    /// <summary>The operator selected Full access.</summary>
    FullAccess,
    /// <summary>The request expired without approval.</summary>
    Expired
}
