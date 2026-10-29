using Oscova.Agents.Credentials;

namespace Oscova.Agents.Core.ApiContracts.OAuth;

public sealed class OAuthProviderAdminConfiguration
{
    public IReadOnlyList<OAuthProviderAdminEntry> Providers { get; set; } = [];
}

/// <summary>Exposes account metadata and safe configuration state to the host UI.</summary>
public sealed class OAuthProviderAdminEntry
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Services { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public IReadOnlyList<CredentialFieldDefinition> Fields { get; set; } = [];
    public string? ClientId { get; set; }
    public bool HasClientSecret { get; set; }
    public bool IsConfigured { get; set; }
}
