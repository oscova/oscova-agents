using System.ComponentModel.DataAnnotations;

namespace Oscova.Agents.Core.ApiContracts.Admin;

/// <summary>Provides all fields required to create a new user account.</summary>
public sealed class CreateUserRequest
{
    [Required]
    [MinLength(3)]
    public string Username { get; set; } = string.Empty;

    [Required]
    [MinLength(1)]
    public string FullName { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    [MinLength(6)]
    public string Password { get; set; } = string.Empty;

    [Required]
    public string ConfirmPassword { get; set; } = string.Empty;

    public ISet<string> SelectedRoles { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
