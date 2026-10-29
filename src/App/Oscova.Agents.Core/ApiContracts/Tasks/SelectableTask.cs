namespace Oscova.Agents.Core.ApiContracts.Tasks;

/// <summary>A task offered as a predecessor option when configuring an after-task trigger.</summary>
public sealed class SelectableTask
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}
