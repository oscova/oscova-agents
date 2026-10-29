namespace Oscova.Agents.Core.ApiContracts.Chat;

/// <summary>Submits one or more enum elicitation field values.</summary>
public sealed class SubmitEnumElicitationRequest
{
    public IDictionary<string, string> Values { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public bool IsCancel { get; set; }
}
