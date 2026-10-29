namespace Oscova.Agents.Core.Secrets;

public sealed class CredentialListItem
{
    public string CredentialKey { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>True when at least one field has a usable stored value or environment-variable name.</summary>
    public bool IsSet { get; set; }

    public bool IsManaged { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
