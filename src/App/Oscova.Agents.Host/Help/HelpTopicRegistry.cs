namespace Oscova.Agents.Host.Help;

// Single source of truth mapping topic keys to their markdown files and panel icons.
// The panel title is read from each file's first heading, so the registry carries no title.
public static class HelpTopicRegistry
{
    private static readonly Dictionary<string, HelpTopicDescriptor> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [HelpTopics.AdminSetup] = new("admin-setup.md", "lucide:user-plus"),
        [HelpTopics.Onboarding] = new("onboarding.md", "lucide:sparkles"),
        [HelpTopics.ChatOptions] = new("chat-options.md", "lucide:sliders-horizontal"),
        [HelpTopics.ChatTable] = new("dialog.chat-table.md", "lucide:table"),
        [HelpTopics.Dashboard] = new("dashboard.md", "lucide:layout-dashboard"),
        [HelpTopics.MetricsDashboard] = new("metrics-dashboard.md", "lucide:gauge"),
        [HelpTopics.Achievements] = new("achievements.md", "lucide:award"),
        [HelpTopics.Profile] = new("profile.md", "lucide:user-round"),
        [HelpTopics.VaultPassword] = new("vault-password.md", "lucide:key-round"),
        [HelpTopics.SettingsGeneral] = new("settings.general.md", "lucide:settings-2"),
        [HelpTopics.SettingsNotifications] = new("settings.notifications.md", "lucide:bell"),
        [HelpTopics.SettingsMainAgent] = new("settings.main-agent.md", "lucide:bot"),
        [HelpTopics.SettingsAgents] = new("settings.agents.md", "lucide:users"),
        [HelpTopics.SettingsFiles] = new("settings.files.md", "lucide:folder-open"),
        [HelpTopics.SettingsImportExport] = new("settings.import-export.md", "lucide:archive"),
        [HelpTopics.SettingsCredentials] = new("settings.credentials.md", "lucide:key-round"),
        [HelpTopics.SettingsConnectedAccounts] = new("settings.connected-accounts.md", "lucide:link"),
        [HelpTopics.SettingsNetwork] = new("settings.network.md", "lucide:wifi"),
        [HelpTopics.SettingsPrivacy] = new("settings.privacy.md", "lucide:shield-check"),
        [HelpTopics.SettingsHooks] = new("settings.hooks.md", "lucide:webhook"),
        [HelpTopics.SettingsMcp] = new("settings.mcp.md", "lucide:plug"),
        [HelpTopics.SettingsModelProviders] = new("settings.model-providers.md", "lucide:server"),
        [HelpTopics.SettingsTools] = new("settings.tools.md", "lucide:wrench"),
        [HelpTopics.SettingsPlugins] = new("settings.plugins.md", "lucide:blocks"),
        [HelpTopics.SettingsExecution] = new("settings.execution.md", "lucide:square-terminal"),
        [HelpTopics.SettingsSkills] = new("settings.skills.md", "lucide:square-terminal"),
        [HelpTopics.SettingsPrompts] = new("settings.prompts.md", "lucide:file-text"),
        [HelpTopics.SettingsAvatar] = new("settings.avatar.md", "lucide:user-round"),
        [HelpTopics.SettingsSpeech] = new("settings.speech.md", "lucide:mic"),
        [HelpTopics.AgentTasks] = new("agent-tasks.md", "lucide:calendar-clock"),
        [HelpTopics.Admin] = new("admin.md", "lucide:shield"),
        [HelpTopics.ChatMemory] = new("chat-memory.md", "lucide:brain"),
        [HelpTopics.LogsDialog] = new("logs-dialog.md", "lucide:scroll-text"),
        [HelpTopics.SessionInspector] = new("session-inspector.md", "lucide:scan-search"),
        [HelpTopics.EgressAudit] = new("egress-audit.md", "lucide:cloud"),
        [HelpTopics.CommandLine] = new("command-line.md", "lucide:terminal"),
        [HelpTopics.ApplicationUpdate] = new("application-update.md", "lucide:download"),
        [HelpTopics.AvatarUpdate] = new("avatar-update.md", "lucide:download"),
        [HelpTopics.AddProject] = new("dialog.add-project.md"),
        [HelpTopics.RenameConversation] = new("dialog.rename-conversation.md"),
        [HelpTopics.ContextUsage] = new("dialog.context-usage.md"),
        [HelpTopics.ContextWindowWarning] = new("dialog.context-window-warning.md"),
        [HelpTopics.WorkspaceFileDecision] = new("dialog.workspace-file-decision.md"),
        [HelpTopics.EditModelBudget] = new("dialog.edit-model-budget.md"),
        [HelpTopics.ModelBudgetWarning] = new("dialog.model-budget-warning.md"),
        [HelpTopics.ProviderModelDownload] = new("dialog.provider-model-download.md"),
        [HelpTopics.LocalModelDownload] = new("dialog.local-model-download.md"),
        [HelpTopics.ModelProviderEditor] = new("dialog.model-provider-editor.md"),
        [HelpTopics.EmbeddingProviderEditor] = new("dialog.embedding-provider-editor.md"),
        [HelpTopics.ModelProviderDetails] = new("dialog.model-provider-details.md"),
        [HelpTopics.CredentialEditor] = new("dialog.credential-editor.md"),
        [HelpTopics.CreateFileSystemItem] = new("dialog.create-file-system-item.md"),
        [HelpTopics.FileEditor] = new("dialog.file-editor.md"),
        [HelpTopics.FileBackupHistory] = new("dialog.file-backup-history.md"),
        [HelpTopics.PromptEditor] = new("dialog.prompt-editor.md"),
        [HelpTopics.HookEditor] = new("dialog.hook-editor.md"),
        [HelpTopics.HookApiKeyEditor] = new("dialog.hook-api-key-editor.md"),
        [HelpTopics.HookSecretEditor] = new("dialog.hook-secret-editor.md"),
        [HelpTopics.AgentImport] = new("dialog.agent-import.md"),
        [HelpTopics.SkillImport] = new("dialog.skill-import.md"),
        [HelpTopics.SkillRegistrySources] = new("dialog.skill-registry-sources.md"),
        [HelpTopics.SkillRegistryBrowser] = new("dialog.skill-registry-browser.md"),
        [HelpTopics.McpImport] = new("dialog.mcp-import.md"),
        [HelpTopics.McpRegistryBrowser] = new("dialog.mcp-registry-browser.md"),
        [HelpTopics.McpServerEditor] = new("dialog.mcp-server-editor.md"),
        [HelpTopics.McpServerDetails] = new("dialog.mcp-server-details.md"),
        [HelpTopics.PluginRegistryBrowser] = new("dialog.plugin-registry-browser.md"),
        [HelpTopics.PluginDetails] = new("dialog.plugin-details.md"),
        [HelpTopics.NotificationInbox] = new("dialog.notification-inbox.md"),
        [HelpTopics.NotificationDetails] = new("dialog.notification-details.md"),
        [HelpTopics.ImageGallery] = new("dialog.image-gallery.md"),
        [HelpTopics.ToolGovernance] = new("dialog.tool-governance.md"),
        [HelpTopics.PairAvatarEngine] = new("dialog.pair-avatar-engine.md"),
        [HelpTopics.AvatarMessageLayout] = new("dialog.avatar-message-layout.md"),
        [HelpTopics.ThirdPartyLicenses] = new("dialog.third-party-licenses.md"),
        [HelpTopics.CreateUser] = new("dialog.create-user.md"),
        [HelpTopics.EditUser] = new("dialog.edit-user.md"),
        [HelpTopics.OAuthProviders] = new("dialog.oauth-providers.md"),
        [HelpTopics.GitHubSkillSearch] = new("dialog.git-hub-skill-search.md"),
        [HelpTopics.DuressCredentials] = new("dialog.duress-credentials.md"),
        [HelpTopics.ExportEnvironment] = new("dialog.export-environment.md"),
        [HelpTopics.RestoreBackup] = new("dialog.restore-backup.md"),
        [HelpTopics.EnvironmentReset] = new("dialog.environment-reset.md"),
        [HelpTopics.AgentTaskEditor] = new("dialog.agent-task-editor.md"),
        [HelpTopics.AgentTaskDetails] = new("dialog.agent-task-details.md"),
        [HelpTopics.AgentTaskRuns] = new("dialog.agent-task-runs.md"),
        [HelpTopics.TaskRunScope] = new("dialog.task-run-scope.md"),
    };

    public static HelpTopicDescriptor? Resolve(string? topicKey)
    {
        if (string.IsNullOrWhiteSpace(topicKey)) return null;
        return Map.TryGetValue(topicKey, out var d) ? d : null;
    }

    // Reverse lookup: turns an in-document link target ("settings.agents.md") into its topic key.
    public static string? ResolveKeyByFile(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        foreach (var pair in Map)
        {
            if (string.Equals(pair.Value.RelativePath, fileName, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Key;
            }
        }

        return null;
    }

    // Resolves the help topic for a settings section, or null when the section has none.
    public static string? ResolveForSection(string? section)
    {
        if (string.Equals(section, "general", StringComparison.OrdinalIgnoreCase))
            return HelpTopics.SettingsGeneral;
        if (string.Equals(section, "notifications", StringComparison.OrdinalIgnoreCase))
            return HelpTopics.SettingsNotifications;
        if (string.Equals(section, "agent", StringComparison.OrdinalIgnoreCase))
            return HelpTopics.SettingsMainAgent;
        if (string.Equals(section, "agents", StringComparison.OrdinalIgnoreCase))
            return HelpTopics.SettingsAgents;
        if (string.Equals(section, "files", StringComparison.OrdinalIgnoreCase))
            return HelpTopics.SettingsFiles;
        if (string.Equals(section, "importexport", StringComparison.OrdinalIgnoreCase))
            return HelpTopics.SettingsImportExport;
        if (string.Equals(section, "credentials", StringComparison.OrdinalIgnoreCase))
            return HelpTopics.SettingsCredentials;
        if (string.Equals(section, "connectedaccounts", StringComparison.OrdinalIgnoreCase))
            return HelpTopics.SettingsConnectedAccounts;
        if (string.Equals(section, "network", StringComparison.OrdinalIgnoreCase))
            return HelpTopics.SettingsNetwork;
        if (string.Equals(section, "privacy", StringComparison.OrdinalIgnoreCase))
            return HelpTopics.SettingsPrivacy;
        if (string.Equals(section, "hooks", StringComparison.OrdinalIgnoreCase))
            return HelpTopics.SettingsHooks;
        if (string.Equals(section, "mcp", StringComparison.OrdinalIgnoreCase))
            return HelpTopics.SettingsMcp;
        if (string.Equals(section, "providers", StringComparison.OrdinalIgnoreCase))
            return HelpTopics.SettingsModelProviders;
        if (string.Equals(section, "tools", StringComparison.OrdinalIgnoreCase))
            return HelpTopics.SettingsTools;
        if (string.Equals(section, "plugins", StringComparison.OrdinalIgnoreCase))
            return HelpTopics.SettingsPlugins;
        if (string.Equals(section, "execution", StringComparison.OrdinalIgnoreCase)) { return HelpTopics.SettingsExecution; }

        if (string.Equals(section, "skills", StringComparison.OrdinalIgnoreCase))
            return HelpTopics.SettingsSkills;
        if (string.Equals(section, "compaction", StringComparison.OrdinalIgnoreCase))
            return HelpTopics.SettingsPrompts;
        if (string.Equals(section, "avatar", StringComparison.OrdinalIgnoreCase))
            return HelpTopics.SettingsAvatar;
        if (string.Equals(section, "speech", StringComparison.OrdinalIgnoreCase))
            return HelpTopics.SettingsSpeech;
        return null;
    }
}
