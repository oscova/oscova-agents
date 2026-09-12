namespace Oscova.Agents.Policies;

/// <summary>
/// Whether a resolved endpoint stays local or reaches public-cloud infrastructure.
/// </summary>
public enum EgressClass
{
    /// <summary>
    /// The endpoint is local or on the user's private network.
    /// </summary>
    Local,

    /// <summary>
    /// The endpoint reaches public-cloud infrastructure.
    /// </summary>
    Cloud
}
