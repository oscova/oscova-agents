namespace Oscova.Agents.Core.ApiContracts.Agents;

/// <summary>A user available for selection in the agent sharing configuration.</summary>
public sealed class SelectableUser
{
    public string Id { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string? FullName { get; set; }
}
