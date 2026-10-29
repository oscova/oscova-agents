namespace Oscova.Agents.Core.ApiContracts.Tasks;

/// <summary>An agent available for selection when creating or editing a task.</summary>
public sealed class SelectableAgent
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}
