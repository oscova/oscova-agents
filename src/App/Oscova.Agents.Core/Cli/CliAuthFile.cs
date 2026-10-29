namespace Oscova.Agents.Core.Cli;

/// <summary>Persists per-OS-user account and device authentication state.</summary>
public sealed class CliAuthFile
{
    public int Version { get; set; } = 1;
    public string? CurrentHost { get; set; }
    public IList<CliAuthHostEntry> Hosts { get; set; } = [];
    public IList<CliNodeEntry> Nodes { get; set; } = [];
}

/// <summary>Stores the credential needed to reconnect one account-owned device to its server.</summary>
public sealed record CliNodeEntry(string Host, string UserId, string NodeId, string Token);

/// <summary>
/// One authenticated host entry stored in the local CLI auth file.
/// </summary>
public sealed class CliAuthHostEntry
{
    public string Host { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public string IdentityUserId { get; set; } = string.Empty;
    public string IdentityUserName { get; set; } = string.Empty;
    public DateTimeOffset IssuedAtUtc { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public string[] Scopes { get; set; } = [];
}
