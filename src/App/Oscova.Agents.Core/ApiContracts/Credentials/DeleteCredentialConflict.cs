namespace Oscova.Agents.Core.ApiContracts.Credentials;

/// <summary>Returned (409 Conflict) when a credential deletion is blocked by dependent settings.</summary>
public sealed class DeleteCredentialConflict
{
    public string Message { get; set; } = string.Empty;
    public IReadOnlyList<string> AffectedSettings { get; set; } = [];
}
