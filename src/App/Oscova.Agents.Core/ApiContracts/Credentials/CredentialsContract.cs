namespace Oscova.Agents.Core.ApiContracts.Credentials;

/// <summary>The projected Credentials view: declared slots merged with saved rows, plus free-form credentials.</summary>
public sealed class CredentialsContract
{
    /// <summary>Declared slots for the current user's scope.</summary>
    public IList<CredentialSlotContract> Slots { get; set; } = [];

    /// <summary>Saved free-form credentials that do not match any declared slot.</summary>
    public IList<CredentialSlotContract> FreeForm { get; set; } = [];
}
