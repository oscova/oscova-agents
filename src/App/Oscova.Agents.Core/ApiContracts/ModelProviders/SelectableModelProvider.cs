namespace Oscova.Agents.Core.ApiContracts.ModelProviders;

/// <summary>
/// Model provider option that can be selected by agent and task editors.
/// </summary>
public sealed class SelectableModelProvider
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ModelId { get; set; }
}