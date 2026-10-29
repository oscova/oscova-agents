namespace Oscova.Agents.Core.ApiContracts.Chat;

/// <summary>Submits one or more numeric elicitation field values.</summary>
public sealed class SubmitNumberElicitationRequest
{
    public IDictionary<string, NumberElicitationValue> Values { get; set; } = new Dictionary<string, NumberElicitationValue>(StringComparer.OrdinalIgnoreCase);

    public bool IsCancel { get; set; }
}
