namespace Oscova.Agents.Core;

/// <summary>
/// File and directory names used under app_data/users/&lt;userId&gt;/.
/// Single source of truth for the per-user file layout.
/// </summary>
public static class UserFileNames
{
    // Top-level directories under users/<userId>/
    public const string AgentsDirectory = "agents";
    public const string TasksDirectory = "tasks";
    public const string MemoryDirectory = "memory";
    public const string BacklogDirectory = "backlog";
    public const string PromptsDirectory = "prompts";
    public const string SkillsDirectory = "skills";
    public const string WorkspaceDirectory = "workspace";
    public const string ChatDirectory = "chat";
    public const string LogsDirectory = "logs";
    public const string SettingsDirectory = "settings";
    public const string MetricsDirectory = "metrics";
    public const string AuditDirectory = "audit";
    public const string FileHistoryDirectory = "file-history";
    public const string DefaultWorkspaceDirectory = "default";
    public const string LibraryWorkspaceDirectory = "library";
    public const string ToolsWorkspaceDirectory = "tools";

    // Reserved sub-directories
    public const string MainAgentDirectory = "main";
    public const string SharedAgentsDirectory = "shared";
    public const string AgentPrivateDirectory = "private";

    // Known file names
    public const string AgentFile = "agent.md.enc";
    public const string AgentToolPoliciesFile = "tool-policies.yaml.enc";
    public const string ProfileImageFile = "profile.image";
    public const string TaskFile = "task.md.enc";
    public const string TaskRunsFile = "runs.jsonl";
    public const string RunsDirectory = "runs";
    public const string SessionIndexFile = "index.jsonl";
    public const string SessionTranscriptExtension = ".jsonl";
    public const string MetricsEventsFile = "events.jsonl";
    public const string EgressLogFile = "egress.jsonl";
    public const string SharedAgentContextFile = "context.md.enc";
    public const string LibraryIndexManifestFile = "library-index.json.enc";

    /// <summary>Identifies encrypted user configuration by its canonical relative location.</summary>
    public static bool IsEncryptedConfigurationPath(string relativePath)
    {
        var segments = relativePath.Replace('\\', '/').Split('/');
        if (segments.Length == 2 &&
            string.Equals(segments[0], SettingsDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return segments[1].EndsWith(".yaml.enc", StringComparison.OrdinalIgnoreCase);
        }

        if (segments.Length == 2 &&
            string.Equals(segments[0], PromptsDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return segments[1].EndsWith(".md.enc", StringComparison.OrdinalIgnoreCase);
        }

        if (segments.Length == 3 &&
            string.Equals(segments[0], TasksDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(segments[2], TaskFile, StringComparison.OrdinalIgnoreCase);
        }

        if (segments.Length == 3 &&
            string.Equals(segments[0], AgentsDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(segments[2], AgentFile, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(segments[2], AgentToolPoliciesFile, StringComparison.OrdinalIgnoreCase);
        }

        return segments.Length == 4 &&
               string.Equals(segments[0], AgentsDirectory, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(segments[1], SharedAgentsDirectory, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(segments[3], SharedAgentContextFile, StringComparison.OrdinalIgnoreCase);
    }
}
