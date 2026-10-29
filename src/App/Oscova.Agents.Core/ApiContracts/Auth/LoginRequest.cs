namespace Oscova.Agents.Core.ApiContracts.Auth;

/// <summary>Captures the credentials submitted for password-based sign-in.</summary>
public sealed class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}