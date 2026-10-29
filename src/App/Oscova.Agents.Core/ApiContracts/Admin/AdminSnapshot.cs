using Oscova.Agents.Core.Settings;

namespace Oscova.Agents.Core.ApiContracts.Admin;

/// <summary>Top-level admin panel response containing auth settings and all users.</summary>
public sealed class AdminSnapshot
{
    public AuthMode AuthMode { get; set; }
    public bool RequirePinForHome { get; set; }
    public bool HasAdminPin { get; set; }
    public IReadOnlyList<string> AvailableRoles { get; set; } = [];
    public IReadOnlyList<UserEntry> Users { get; set; } = [];
}
