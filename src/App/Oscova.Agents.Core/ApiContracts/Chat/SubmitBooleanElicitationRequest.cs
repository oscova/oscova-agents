namespace Oscova.Agents.Core.ApiContracts.Chat;

/// <summary>Submits a boolean elicitation response.</summary>
public sealed class SubmitBooleanElicitationRequest
{
    public bool? Value { get; set; }

    public bool IsCancel { get; set; }
}
