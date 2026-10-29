namespace Oscova.Agents.Infrastructure.Auth;

/// <summary>Keeps login and vault cooldowns independent of each other and Identity credential updates.</summary>
public sealed class SignInAttemptState
{
    public string UserId { get; set; } = string.Empty;
    public CredentialCategory Category { get; set; }
    public int FailureCount { get; set; }
    public long? LastFailureUnixMilliseconds { get; set; }
    public long? RetryUntilUnixMilliseconds { get; set; }
}

/// <summary>Separates account sign-in from vault password verification.</summary>
public enum CredentialCategory
{
    Login,
    Vault
}
