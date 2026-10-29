namespace Oscova.Agents.Core.ApiContracts.Credentials;

/// <summary>A credential slot — a declared slot or a free-form credential — with its fields and saved state.</summary>
public sealed class CredentialSlotContract
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>"User" or "System".</summary>
    public string Scope { get; set; } = "User";

    public bool IsManaged { get; set; }

    /// <summary>The saved alias when a row exists; null otherwise.</summary>
    public string? Alias { get; set; }

    /// <summary>True when a stored row exists for this key.</summary>
    public bool RowExists { get; set; }

    public IList<CredentialFieldContract> Fields { get; set; } = [];
}
