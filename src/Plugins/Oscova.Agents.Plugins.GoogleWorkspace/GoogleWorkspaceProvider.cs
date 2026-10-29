using Oscova.Agents.Credentials;

namespace Oscova.Agents.Plugins.GoogleWorkspace;

/// <summary>Declares Google's consent endpoints, permissions and app credential fields.</summary>
internal static class GoogleWorkspaceProvider
{
    internal const string Id = "google";

    internal static OAuthProviderDefinition Definition { get; } = new()
    {
        Id = Id,
        DisplayName = "Google",
        Services = "Gmail, Calendar, YouTube",
        Description = "Gmail, Calendar, YouTube",
        AppCredential = new CredentialDeclaration
        {
            Key = "oauth-provider:google",
            Label = "Google OAuth",
            Description = "Client id and secret for the Google OAuth app used by connected accounts.",
            Scope = CredentialScope.System,
            Fields =
            [
                new() { Name = OAuthProviderDefinition.ClientIdField, Label = "Client ID", IsSecret = false },
                new() { Name = OAuthProviderDefinition.ClientSecretField, Label = "Client Secret" }
            ]
        },
        AuthorizationEndpoint = new Uri("https://accounts.google.com/o/oauth2/v2/auth"),
        TokenEndpoint = new Uri("https://oauth2.googleapis.com/token"),
        UserInfoEndpoint = new Uri("https://www.googleapis.com/oauth2/v3/userinfo"),
        Scopes =
        [
            "openid", "email", "profile",
            "https://www.googleapis.com/auth/gmail.readonly",
            "https://www.googleapis.com/auth/gmail.send",
            "https://www.googleapis.com/auth/calendar.readonly",
            "https://www.googleapis.com/auth/youtube.readonly"
        ],
        // Consent with offline access preserves Google's refresh token on reconnect.
        AuthorizationParameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["access_type"] = "offline",
            ["prompt"] = "consent"
        },
        ReadConnectedEmail = json => json.TryGetProperty("email", out var email) ? email.GetString() : null
    };
}
