namespace Oscova.Agents.Core;

/// <summary>
/// Well-known user identifiers used by the system.
/// </summary>
public static class WellKnownUsers
{
    /// <summary>
    /// The synthetic user that owns system-level settings and credentials.
    /// This user is never a real account and must be excluded from user listings and scope initialization.
    /// </summary>
    public const string SystemId = "__system__";
}
