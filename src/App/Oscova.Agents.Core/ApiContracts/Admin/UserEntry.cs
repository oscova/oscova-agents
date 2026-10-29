namespace Oscova.Agents.Core.ApiContracts.Admin;

/// <summary>A single user row in the admin panel user list.</summary>
public sealed class UserEntry
{
    public string Id { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public IList<string> SelectedRoles { get; set; } = [];
    public bool HasPin { get; set; }
    public bool IsCurrentUser { get; set; }
}
