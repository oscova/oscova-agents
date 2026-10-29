using Microsoft.AspNetCore.Identity;
using System.ComponentModel.DataAnnotations;

namespace Oscova.Agents.Infrastructure.Auth;

public class ApplicationUser : IdentityUser
{
    /// <summary>Keeps sign-in names lowercase across setup and account edits.</summary>
    [ProtectedPersonalData]
    public override string? UserName
    {
        get => base.UserName;
        set => base.UserName = value?.ToLowerInvariant();
    }

    [PersonalData]
    [MaxLength(256)]
    public string FullName { get; set; } = string.Empty;

    [PersonalData]
    [MaxLength(256)]
    public string? PinHash { get; set; }

    /// <summary>Versioned Vault-Password-wrapped envelope for this user's data key.</summary>
    public string? VaultKeyEnvelope { get; set; }

    /// <summary>Versioned owner and duress slots kept in step with the account password.</summary>
    public string DuressRecord { get; set; } = string.Empty;

    /// <summary>
    /// Increments whenever existing CLI tokens for this user should be revoked.
    /// </summary>
    public int CliTokenVersion { get; set; }
}
