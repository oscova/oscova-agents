using Oscova.Agents.Credentials;

namespace Oscova.Agents.Plugins.MicrosoftGraph;

/// <summary>Declares Microsoft consent endpoints, permissions and app credential fields.</summary>
internal static class MicrosoftGraphProvider
{
    internal const string Id = "microsoft";

    internal static OAuthProviderDefinition Definition { get; } = new()
    {
        Id = Id,
        DisplayName = "Microsoft",
        Services = "Outlook, To-Do, Microsoft Graph",
        Description = "Outlook, To-Do, Graph integrations",
        AppCredential = new CredentialDeclaration
        {
            Key = "oauth-provider:microsoft",
            Label = "Microsoft OAuth",
            Description = "Client id and secret for the Microsoft OAuth app used by connected accounts.",
            Scope = CredentialScope.System,
            Fields =
            [
                new() { Name = OAuthProviderDefinition.ClientIdField, Label = "Client ID", IsSecret = false },
                new() { Name = OAuthProviderDefinition.ClientSecretField, Label = "Client Secret" }
            ]
        },
        AuthorizationEndpoint = new Uri("https://login.microsoftonline.com/common/oauth2/v2.0/authorize"),
        TokenEndpoint = new Uri("https://login.microsoftonline.com/common/oauth2/v2.0/token"),
        UserInfoEndpoint = new Uri("https://graph.microsoft.com/v1.0/me?$select=mail,userPrincipalName"),
        Scopes =
        [
            "openid", "email", "profile", "offline_access",
            "https://graph.microsoft.com/Mail.Read",
            "https://graph.microsoft.com/Mail.Send",
            "https://graph.microsoft.com/Tasks.ReadWrite",
            "https://graph.microsoft.com/User.Read"
        ],
        ReadConnectedEmail = json =>
        {
            var mail = json.TryGetProperty("mail", out var value) ? value.GetString() : null;
            return !string.IsNullOrWhiteSpace(mail) ? mail
                : json.TryGetProperty("userPrincipalName", out value) ? value.GetString() : null;
        }
    };
}
