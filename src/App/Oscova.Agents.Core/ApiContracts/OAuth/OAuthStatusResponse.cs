namespace Oscova.Agents.Core.ApiContracts.OAuth;

public sealed class OAuthStatusResponse
{
    public IReadOnlyList<OAuthProviderStatus> Providers { get; set; } = [];
}

/// <summary>Exposes account metadata and safe configuration state to the host UI.</summary>
public sealed class OAuthProviderStatus
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Services { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsConfigured { get; set; }
    public bool IsConnected { get; set; }
    public string? ConnectedEmail { get; set; }
}
