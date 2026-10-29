using Oscova.Agents.Plugins;
using Microsoft.Extensions.Localization;
using Oscova.Agents.Host.Localization;
using Oscova.Agents.Host.Services.UI;

namespace Oscova.Agents.Host.Components.Settings;

public sealed class SettingsPageRegistry(PluginSettingsCatalog panels, PluginPresentationService presentation)
{
    public sealed record SettingsCategoryInfo(
        string Key,
        string RouteSlug,
        string LabelKey,
        string LabelDefault,
        string Icon,
        string DescriptionKey,
        string DescriptionDefault,
        string[]? RouteAliases = null);

    public sealed record SettingsSectionInfo(
        string Key,
        string RouteSlug,
        string LabelKey,
        string LabelDefault,
        string Icon,
        string CategoryKey,
        string DescriptionKey,
        string DescriptionDefault,
        bool RequiresAdmin = false,
        string[]? RouteAliases = null,
        PluginSettingsCatalog.Entry? PluginPage = null,
        string? HelpTopic = null,
        string? IconImageSource = null);

    public static readonly SettingsCategoryInfo[] Categories =
    [
        new("workspace", "preferences", SettingsTextKeys.WorkspaceCategoryLabel, "Preferences", "lucide:layout-dashboard", SettingsTextKeys.WorkspaceCategoryDescription, "Appearance, language, notifications, persona, and voice.", ["workspace"]),
        new("intelligence", "agents-ai", SettingsTextKeys.IntelligenceCategoryLabel, "Agents & AI", "lucide:brain-circuit", SettingsTextKeys.IntelligenceCategoryDescription, "Agents, model providers, instructions, skills, and tools.", ["intelligence"]),
        new("connections", "accounts-access", SettingsTextKeys.ConnectionsCategoryLabel, "Accounts & Access", "lucide:globe-2", SettingsTextKeys.ConnectionsCategoryDescription, "Connected accounts, credentials, network access, and webhooks.", ["connections"]),
        new("channels", "channels", SettingsTextKeys.ChannelsCategoryLabel, "Channels", "lucide:radio-tower", SettingsTextKeys.ChannelsCategoryDescription, "Chat channels and bot integrations."),
        new("data", "files-backup", SettingsTextKeys.DataCategoryLabel, "Files & Backup", "lucide:folder-kanban", SettingsTextKeys.DataCategoryDescription, "Files, imports, exports, and backups.", ["data"])
    ];

    private static readonly SettingsSectionInfo[] BuiltInSections =
    [
        new("general", "general", SettingsTextKeys.GeneralLabel, "General", "lucide:settings-2", "workspace", SettingsTextKeys.GeneralDescription, "Configure general application preferences and appearance."),
        new("notifications", "notifications", SettingsTextKeys.NotificationsTitle, "Notifications", "lucide:bell", "workspace", SettingsTextKeys.NotificationsSectionDescription, "Control notification delivery and subtle UI sound cues."),
        new("avatar", "avatar", SettingsTextKeys.AvatarLabel, "Avatar", "lucide:user-round", "workspace", SettingsTextKeys.AvatarDescription, "Manage the shared avatar runtime and bridge settings.", RequiresAdmin: true),
        new("speech", "speech", SettingsTextKeys.SpeechLabel, "Speech", "lucide:mic", "workspace", SettingsTextKeys.SpeechDescription, "Configure text-to-speech and speech recognition options."),
        new("providers", "model-providers", SettingsTextKeys.ProvidersLabel, "Model Providers", "lucide:server", "intelligence", SettingsTextKeys.ProvidersDescription, "Manage model provider connections and API keys.", RouteAliases: ["providers", "ai-providers"]),
        new("agent", "main-agent", SettingsTextKeys.AgentLabel, "Main Agent", "lucide:bot", "intelligence", SettingsTextKeys.AgentDescription, "Configure the main AI assistant behavior and instructions.", RouteAliases: ["agent"]),
        new("agents", "agents", SettingsTextKeys.AgentsLabel, "Agents", "lucide:users", "intelligence", SettingsTextKeys.AgentsDescription, "Manage additional AI agents and their configurations."),
        new("compaction", "instructions", SettingsTextKeys.CompactionLabel, "Instructions", "lucide:file-text", "intelligence", SettingsTextKeys.CompactionDescription, "Edit the saved instructions used for compaction and memory.", RouteAliases: ["compaction"]),
        new("skills", "skills", SettingsTextKeys.SkillsLabel, "Skills", "lucide:square-terminal", "intelligence", SettingsTextKeys.SkillsDescription, "Find, install, share, and remove agent skills."),
        new("execution", "execution", SettingsTextKeys.ExecutionLabel, "Execution", "lucide:cpu", "intelligence", SettingsTextKeys.ExecutionDescription, "Choose where shell commands and bundled skill scripts run.", RequiresAdmin: true),
        new("tools", "tools", SettingsTextKeys.ToolsLabel, "Tools", "lucide:wrench", "intelligence", SettingsTextKeys.ToolsDescription, "Enable or disable tools available to the AI assistant."),
        new("plugins", "plugins", SettingsTextKeys.PluginsLabel, "Plugins", "lucide:blocks", "intelligence", SettingsTextKeys.PluginsDescription, "Manage installed extensions permitted to run after restart.", RequiresAdmin: true),
        new("mcp", "tool-servers", SettingsTextKeys.McpLabel, "MCP Servers", "lucide:plug", "intelligence", SettingsTextKeys.McpDescription, "Connect external tool servers that use the Model Context Protocol.", RouteAliases: ["mcp"]),
        new("network", "network", SettingsTextKeys.NetworkLabel, "Network", "lucide:wifi", "connections", SettingsTextKeys.NetworkDescription, "Adjust network, proxy, and connection settings.", RequiresAdmin: true),
        new("privacy", "privacy", SettingsTextKeys.PrivacyLabel, "Privacy", "lucide:shield-check", "connections", SettingsTextKeys.PrivacyDescription, "Control offline mode, automatic sign-out, and what your agent can send out."),
        new("hooks", "hooks", SettingsTextKeys.HooksLabel, "Hooks", "lucide:webhook", "connections", SettingsTextKeys.HooksDescription, "Configure outbound lifecycle hooks and inbound task webhooks.", RequiresAdmin: true),
        new("connectedaccounts", "connected-accounts", SettingsTextKeys.ConnectedAccountsLabel, "Connected Accounts", "lucide:link", "connections", SettingsTextKeys.ConnectedAccountsDescription, "Connect Google and Microsoft accounts for OAuth-based integrations.", RouteAliases: ["connectedaccounts"]),
        new("credentials", "credentials", SettingsTextKeys.CredentialsLabel, "Credentials", "lucide:key-round", "connections", SettingsTextKeys.CredentialsDescription, "Manage saved API keys and tokens."),
        new("importexport", "import-export", SettingsTextKeys.ImportExportLabel, "Backup", "lucide:archive", "data", SettingsTextKeys.ImportExportDescription, "Export or reset restorable app data.", RequiresAdmin: true, RouteAliases: ["importexport"]),
        new("files", "files", SettingsTextKeys.FilesLabel, "Files", "lucide:folder-open", "data", SettingsTextKeys.FilesDescription, "Manage file storage locations and upload preferences.")
    ];

    /// <summary>Combines built-in entries with admitted independent plugin pages.</summary>
    public IReadOnlyList<SettingsSectionInfo> Sections =>
    [
        .. BuiltInSections,
        .. panels.Entries.Where(entry => entry.Panel.SlotId == PluginSettingsSlots.Page).Select(entry =>
        {
            var page = entry.Panel.Page!;
            return new SettingsSectionInfo($"plugin:{entry.Plugin.Manifest.Id}:{entry.Panel.Id}",
                $"plugins/{Uri.EscapeDataString(entry.Plugin.Manifest.Id)}/{Uri.EscapeDataString(entry.Panel.Id)}",
                page.TitleKey ?? string.Empty, page.Title, "lucide:settings-2", page.CategoryId,
                page.DescriptionKey ?? string.Empty, page.Description ?? string.Empty,
                RequiresAdmin: entry.Panel.Access == PluginSettingsAccess.Operator, PluginPage: entry,
                HelpTopic: entry.Panel.Help is { } help ? PluginPresentationService.TopicKey(entry.Plugin, help) : null,
                IconImageSource: presentation.Icon(entry.Plugin, page.IconResourceName));
        })
    ];

    public string GetSectionLabel(string key, IStringLocalizer localizer)
    {
        var section = GetSectionInfo(key);
        if (section is null) { return localizer.GetOrDefault(SettingsTextKeys.GeneralLabel, "General"); }

        return section.PluginPage is { } entry
            ? presentation.Text(entry.Plugin, section.LabelKey, section.LabelDefault)
            : localizer.GetOrDefault(section.LabelKey, section.LabelDefault);
    }

    public string GetSectionDescription(string key, IStringLocalizer localizer)
    {
        var section = GetSectionInfo(key);
        if (section is null) { return string.Empty; }

        return section.PluginPage is { } entry
            ? presentation.Text(entry.Plugin, section.DescriptionKey, section.DescriptionDefault)
            : localizer.GetOrDefault(section.DescriptionKey, section.DescriptionDefault);
    }

    public SettingsSectionInfo? GetSectionInfo(string key) =>
        Sections.FirstOrDefault(section => MatchesRouteToken(section.Key, key) ||
                                           MatchesRouteToken(section.RouteSlug, key) ||
                                           MatchesRouteAlias(section.RouteAliases, key));

    public SettingsSectionInfo? GetSectionForRoute(string? categorySlug, string? sectionSlug)
    {
        var section = Sections.FirstOrDefault(candidate =>
            (MatchesRouteToken(candidate.Key, sectionSlug) || MatchesRouteToken(candidate.RouteSlug, sectionSlug) ||
             MatchesRouteAlias(candidate.RouteAliases, sectionSlug)) &&
            GetCategoryInfo(candidate.CategoryKey) is { } categoryInfo && CategoryMatchesRoute(categoryInfo, categorySlug));
        if (section is null)
        {
            return null;
        }

        var category = GetCategoryInfo(section.CategoryKey);
        if (category is null || !CategoryMatchesRoute(category, categorySlug))
        {
            return null;
        }

        return section;
    }

    /// <summary>Resolves only admitted plugin identities in their declared category.</summary>
    public SettingsSectionInfo? GetPluginPageForRoute(string? category, string? pluginId, string? panelId) =>
        Sections.FirstOrDefault(section => section.PluginPage is { } entry &&
            string.Equals(entry.Plugin.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(entry.Panel.Id, panelId, StringComparison.OrdinalIgnoreCase) &&
            GetCategoryInfo(section.CategoryKey) is { } info && CategoryMatchesRoute(info, category));

    public SettingsCategoryInfo? GetCategoryInfo(string key) =>
        Categories.FirstOrDefault(category => string.Equals(category.Key, key, StringComparison.Ordinal));

    public SettingsCategoryInfo? GetCategoryForSection(string sectionKey)
    {
        var sectionInfo = GetSectionInfo(sectionKey);
        if (sectionInfo is null)
        {
            return null;
        }

        return GetCategoryInfo(sectionInfo.CategoryKey);
    }

    public string GetCategoryLabel(string sectionKey, IStringLocalizer localizer)
    {
        var category = GetCategoryForSection(sectionKey);
        return category is null
            ? localizer.GetOrDefault(SettingsTextKeys.SettingsLabel, "Settings")
            : localizer.GetOrDefault(category.LabelKey, category.LabelDefault);
    }

    public string GetCategoryDescription(string sectionKey, IStringLocalizer localizer)
    {
        var category = GetCategoryForSection(sectionKey);
        return category is null
            ? string.Empty
            : localizer.GetOrDefault(category.DescriptionKey, category.DescriptionDefault);
    }

    public bool CanAccessSection(string sectionKey, bool isAdmin)
    {
        var section = GetSectionInfo(sectionKey);
        return section is not null && (!section.RequiresAdmin || isAdmin);
    }

    public IReadOnlyList<SettingsSectionInfo> GetSectionsForCategory(string categoryKey, bool isAdmin) =>
        Sections
            .Where(section => string.Equals(section.CategoryKey, categoryKey, StringComparison.Ordinal) && (!section.RequiresAdmin || isAdmin))
            .ToArray();

    public string GetSectionRoute(string sectionKey)
    {
        var section = GetSectionInfo(sectionKey);
        var category = section is null ? null : GetCategoryInfo(section.CategoryKey);

        return section is null || category is null
            ? "/settings/preferences/general"
            : $"/settings/{category.RouteSlug}/{section.RouteSlug}";
    }

    private static bool CategoryMatchesRoute(SettingsCategoryInfo category, string? routeSlug) =>
        MatchesRouteToken(category.Key, routeSlug) ||
        MatchesRouteToken(category.RouteSlug, routeSlug) ||
        MatchesRouteAlias(category.RouteAliases, routeSlug);

    private static bool MatchesRouteAlias(IEnumerable<string>? aliases, string? routeSlug) =>
        aliases?.Any(alias => MatchesRouteToken(alias, routeSlug)) == true;

    private static bool MatchesRouteToken(string token, string? routeSlug) =>
        string.Equals(token, routeSlug, StringComparison.OrdinalIgnoreCase);
}

