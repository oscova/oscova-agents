namespace Oscova.Agents.Core.Secrets;

public sealed class OAuthTokenData
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public string? ConnectedEmail { get; set; }

    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt.AddMinutes(-2);
}
