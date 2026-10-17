namespace Oscova.Agents.Credentials;

/// <summary>Whether a declared credential belongs to an individual user or the system as a whole.</summary>
public enum CredentialScope
{
    /// <summary>The credential belongs to one user.</summary>
    User,
    /// <summary>The credential belongs to the host.</summary>
    System
}
