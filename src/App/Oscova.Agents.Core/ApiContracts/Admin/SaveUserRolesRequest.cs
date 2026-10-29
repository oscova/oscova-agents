namespace Oscova.Agents.Core.ApiContracts.Admin;

/// <summary>Replaces the role assignments for a user.</summary>
public sealed class SaveUserRolesRequest
{
    public ISet<string> SelectedRoles { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
