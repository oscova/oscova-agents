namespace Oscova.Agents.Core.ApiContracts.Plugins.Registry;

/// <summary>Preserves install failure details needed for an explicit replacement action.</summary>
public sealed record PluginInstallFailure(string Message, string? Code = null,
    string? PluginId = null, string? PackageId = null)
{
    public const string ReplacementRequired = "plugin_replacement_required";
}
