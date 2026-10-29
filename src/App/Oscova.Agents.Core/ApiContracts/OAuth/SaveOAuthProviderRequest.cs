namespace Oscova.Agents.Core.ApiContracts.OAuth;

public sealed class SaveOAuthProviderRequest
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
}
