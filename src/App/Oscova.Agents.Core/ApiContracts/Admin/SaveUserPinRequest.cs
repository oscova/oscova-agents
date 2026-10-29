namespace Oscova.Agents.Core.ApiContracts.Admin;

/// <summary>Carries the new PIN and confirmation for a user PIN change.</summary>
public sealed class SaveUserPinRequest
{
    public string NewPin { get; set; } = string.Empty;
    public string ConfirmPin { get; set; } = string.Empty;
}
