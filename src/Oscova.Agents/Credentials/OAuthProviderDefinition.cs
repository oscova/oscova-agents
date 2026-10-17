using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Oscova.Agents.Credentials;

/// <summary>Describes an OAuth account provider whose consent and tokens are managed by the host.</summary>
public sealed class OAuthProviderDefinition
{
    /// <summary>The app client identifier field.</summary>
    public const string ClientIdField = "clientId";

    /// <summary>The app client secret field.</summary>
    public const string ClientSecretField = "clientSecret";

    /// <summary>Gets the stable provider identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the provider's display title.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Gets the services shown beside the account.</summary>
    public required string Services { get; init; }

    /// <summary>Gets the provider description for app credential setup.</summary>
    public required string Description { get; init; }

    /// <summary>Gets the system credential containing the client ID and secret.</summary>
    public required CredentialDeclaration AppCredential { get; init; }

    /// <summary>Gets the HTTPS consent endpoint.</summary>
    public required Uri AuthorizationEndpoint { get; init; }

    /// <summary>Gets the HTTPS token exchange and refresh endpoint.</summary>
    public required Uri TokenEndpoint { get; init; }

    /// <summary>Gets the HTTPS connected-account identity endpoint.</summary>
    public required Uri UserInfoEndpoint { get; init; }

    /// <summary>Gets the permissions requested during consent.</summary>
    public required IReadOnlyList<string> Scopes { get; init; }

    /// <summary>Gets additional consent parameters without overriding protocol fields.</summary>
    public IReadOnlyDictionary<string, string> AuthorizationParameters { get; init; } = new Dictionary<string, string>();

    /// <summary>Reads the connected email from the identity response without retaining its JSON document.</summary>
    public required Func<JsonElement, string?> ReadConnectedEmail { get; init; }
}
