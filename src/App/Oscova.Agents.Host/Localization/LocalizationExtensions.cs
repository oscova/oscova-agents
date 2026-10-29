using Microsoft.Extensions.Localization;
using Oscova.Agents.Core.ApiContracts.Common;
using Oscova.Agents.Policies;

namespace Oscova.Agents.Host.Localization;

/// <summary>
/// Keeps English defaults visible while resource files are being populated.
/// </summary>
public static class LocalizationExtensions
{
    public static string GetOrDefault(this IStringLocalizer localizer, string key, string fallback)
    {
        var localized = localizer[key];
        return localized.ResourceNotFound ? fallback : localized.Value;
    }

    /// <summary>Returns the shared user-facing label for a network scope.</summary>
    public static string GetNetworkScopeLabel(this IStringLocalizer<SettingsText> localizer, NetworkScope scope)
    {
        return scope switch
        {
            NetworkScope.Incidental => localizer.GetOrDefault(SettingsTextKeys.ToolsNetworkIncidental, "Can reach network"),
            NetworkScope.Egress => localizer.GetOrDefault(SettingsTextKeys.ToolsNetworkEgress, "Internet"),
            _ => localizer.GetOrDefault(SettingsTextKeys.ToolsNetworkNo, "Local only")
        };
    }

    /// <summary>Localizes the reserved labels record readers return in place of a name; other labels pass through.</summary>
    public static string GetRecordLabel(this IStringLocalizer<CommonText> localizer, string label)
    {
        return label switch
        {
            RecordLabelKeys.DeletedAgent => localizer.GetOrDefault(CommonTextKeys.RecordLabelDeletedAgent, "Deleted agent"),
            RecordLabelKeys.DeletedProvider => localizer.GetOrDefault(CommonTextKeys.RecordLabelDeletedProvider, "Deleted provider"),
            RecordLabelKeys.DeletedMcpServer => localizer.GetOrDefault(CommonTextKeys.RecordLabelDeletedMcpServer, "Deleted MCP server"),
            RecordLabelKeys.OwnDestination => localizer.GetOrDefault(CommonTextKeys.RecordLabelOwnDestination, "Own destination"),
            RecordLabelKeys.OtherUsersAgents => localizer.GetOrDefault(CommonTextKeys.RecordLabelOtherUsersAgents, "Other users' agents"),
            RecordLabelKeys.UsersOwnProviders => localizer.GetOrDefault(CommonTextKeys.RecordLabelUsersOwnProviders, "Users' own providers"),
            RecordLabelKeys.UsersOwnModels => localizer.GetOrDefault(CommonTextKeys.RecordLabelUsersOwnModels, "Users' own models"),
            RecordLabelKeys.UsersOwnTools => localizer.GetOrDefault(CommonTextKeys.RecordLabelUsersOwnTools, "Users' own tools"),
            _ => label
        };
    }
}
