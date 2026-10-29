using Microsoft.Agents.AI;
using Microsoft.Extensions.Localization;
using Oscova.Agents.Host.Localization;
using Oscova.Agents.Infrastructure.Tools;
using Oscova.Agents.Infrastructure.Toolsets;
using Oscova.Agents.Providers;
using Oscova.Agents.Toolsets;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Oscova.Agents.Host.Components.Chat.ToolCards;

/// <summary>
/// Describes the stable chrome and summary behavior for a tool-call card family.
/// </summary>
public sealed partial record ToolCardDescriptor(
    ToolCardKind Kind,
    string Icon,
    string IconClass,
    string Title,
    string Subtitle,
    string RunningText,
    string NoArgumentsSummary,
    string ArgumentsSummaryFormat)
{
    private static readonly Dictionary<string, ToolCardKind> BuiltInKinds = new(StringComparer.Ordinal)
    {
        [ToolSearchProvider.SearchToolName] = ToolCardKind.ToolSearch,
        [ToolName<VisualizationToolset>(nameof(VisualizationToolset.ShowAsync))] = ToolCardKind.Visualization,
        [ToolName<VisualizationToolset>(nameof(VisualizationToolset.CreateAsync))] = ToolCardKind.Visualization,
        [ToolName<VisualizationToolset>(nameof(VisualizationToolset.ReadAsync))] = ToolCardKind.FileRead,
        [ToolName<VisualizationToolset>(nameof(VisualizationToolset.EditAsync))] = ToolCardKind.FileWrite,
        [ToolName<VisualizationToolset>(nameof(VisualizationToolset.DeleteAsync))] = ToolCardKind.FileWrite,
        [ToolName<CodingToolset>(nameof(CodingToolset.ReadFile))] = ToolCardKind.FileRead,
        [ToolName<CodingToolset>(nameof(CodingToolset.WriteFile))] = ToolCardKind.FileWrite,
        [ToolName<CodingToolset>(nameof(CodingToolset.EditFile))] = ToolCardKind.FileWrite,
        [ToolName<CodingToolset>(nameof(CodingToolset.ApplyPatch))] = ToolCardKind.FileWrite,
        [ToolName<ShellToolset>(nameof(ShellToolset.ExecCommandAsync))] = ToolCardKind.Shell,
        [ToolName<ShellToolset>(nameof(ShellToolset.StartCommandAsync))] = ToolCardKind.Shell,
        [ToolName<ShellToolset>(nameof(ShellToolset.ReadCommandOutputAsync))] = ToolCardKind.Shell,
        [ToolName<ShellToolset>(nameof(ShellToolset.KillCommandAsync))] = ToolCardKind.Shell,
        [ToolName<SkillRegistryToolset>(nameof(SkillRegistryToolset.SearchSkillsAsync))] = ToolCardKind.SkillSearch,
        [ToolName<SkillRegistryToolset>(nameof(SkillRegistryToolset.InspectSkillAsync))] = ToolCardKind.SkillInspect,
        [AgentSkillsProvider.LoadSkillToolName] = ToolCardKind.SkillInspect,
        [AgentSkillsProvider.ReadSkillResourceToolName] = ToolCardKind.SkillInspect,
        [ToolName<SkillRegistryToolset>(nameof(SkillRegistryToolset.InstallSkillAsync))] = ToolCardKind.SkillInstall,
        [ToolName<SkillAuthoringToolset>(nameof(SkillAuthoringToolset.CreateSkillAsync))] = ToolCardKind.SkillAuthoring,
        [ToolName<SkillAuthoringToolset>(nameof(SkillAuthoringToolset.UpdateSkillAsync))] = ToolCardKind.SkillAuthoring,
        [ToolName<CodingToolset>(nameof(CodingToolset.Grep))] = ToolCardKind.Search,
        [ToolName<CodingToolset>(nameof(CodingToolset.Glob))] = ToolCardKind.Search,
        [ToolName<DirectoryToolset>(nameof(DirectoryToolset.ListDirectory))] = ToolCardKind.Directory,
        [ToolName<PdfToolset>(nameof(PdfToolset.ExtractPdfText))] = ToolCardKind.Document,
        [ToolName<NotificationToolset>(nameof(NotificationToolset.NotifyUserAsync))] = ToolCardKind.Notification,
        [ToolName<ReadLogsTool>(nameof(ReadLogsTool.ReadAsync))] = ToolCardKind.Diagnostics,
    };

    public static ToolCardDescriptor For(string? toolName, IStringLocalizer<ChatText> loc)
    {
        var (qualifier, tool) = string.IsNullOrWhiteSpace(toolName)
            ? (string.Empty, string.Empty)
            : ToolNaming.SplitQualifiedName(toolName);

        var owner = string.IsNullOrEmpty(qualifier) ? string.Empty : ToolNaming.SplitQualifiedName(qualifier).Tool;
        var family = ResolveFamily(owner, tool);
        var subtitle = toolName ?? "tool";
        var noArgumentsSummary = loc.GetOrDefault(ChatTextKeys.ToolCardNoArgumentsSummary, "No arguments supplied.");
        var argumentsSummaryFormat = loc.GetOrDefault(ChatTextKeys.ToolCardArgumentsSummaryFormat, "{0} argument(s) supplied.");
        return family switch
        {
            ToolCardKind.Visualization => new(family, "lucide--sparkles", "text-secondary", loc.GetOrDefault(ChatTextKeys.VisualizationGenerating, "Generating visualization"), subtitle, loc.GetOrDefault(ChatTextKeys.VisualizationGenerating, "Generating visualization"), noArgumentsSummary, argumentsSummaryFormat),
            ToolCardKind.ToolSearch => new(family, "lucide--search", "text-info", loc.GetOrDefault(ChatTextKeys.ToolCardToolSearchTitle, "Loading tools"), subtitle, loc.GetOrDefault(ChatTextKeys.ToolCardSearching, "Searching..."), noArgumentsSummary, argumentsSummaryFormat),
            ToolCardKind.Approval => new(family, "lucide--shield-ellipsis", "text-warning", loc.GetOrDefault(ChatTextKeys.ToolApprovalTitle, "Tool approval required"), subtitle, loc.GetOrDefault(ChatTextKeys.ToolCardRunning, "Working..."), noArgumentsSummary, argumentsSummaryFormat),
            ToolCardKind.Shell => new(family, "lucide--terminal", "text-info", loc.GetOrDefault(ChatTextKeys.ToolCardShellTitle, "Shell command"), subtitle, loc.GetOrDefault(ChatTextKeys.ToolCardShellRunning, "Running command..."), noArgumentsSummary, argumentsSummaryFormat),
            ToolCardKind.FileRead => new(family, "lucide--file-text", "text-primary", loc.GetOrDefault(ChatTextKeys.ToolCardFileReadTitle, "Reading file"), subtitle, loc.GetOrDefault(ChatTextKeys.ToolCardReading, "Reading content..."), noArgumentsSummary, argumentsSummaryFormat),
            ToolCardKind.FileWrite => new(family, "lucide--file-pen", "text-warning", loc.GetOrDefault(ChatTextKeys.ToolCardFileChangeTitle, "Changing file"), subtitle, loc.GetOrDefault(ChatTextKeys.ToolCardApplying, "Applying change..."), noArgumentsSummary, argumentsSummaryFormat),
            ToolCardKind.SkillSearch => new(family, "lucide--sparkles", "text-secondary", loc.GetOrDefault(ChatTextKeys.ToolCardSkillSearchTitle, "Searching skills"), subtitle, loc.GetOrDefault(ChatTextKeys.ToolCardSearching, "Searching..."), noArgumentsSummary, argumentsSummaryFormat),
            ToolCardKind.SkillInspect => new(family, "lucide--book-open-text", "text-secondary", loc.GetOrDefault(ChatTextKeys.ToolCardSkillInspectTitle, "Inspecting skill"), subtitle, loc.GetOrDefault(ChatTextKeys.ToolCardReading, "Reading content..."), noArgumentsSummary, argumentsSummaryFormat),
            ToolCardKind.SkillInstall => new(family, "lucide--badge-plus", "text-secondary", loc.GetOrDefault(ChatTextKeys.ToolCardSkillInstallTitle, "Installing skill"), subtitle, loc.GetOrDefault(ChatTextKeys.ToolCardInstalling, "Installing..."), noArgumentsSummary, argumentsSummaryFormat),
            ToolCardKind.SkillAuthoring => new(family, "lucide--sparkles", "text-secondary", loc.GetOrDefault(ChatTextKeys.ToolCardSkillAuthoringTitle, "Saving skill"), subtitle, loc.GetOrDefault(ChatTextKeys.ToolCardSaving, "Saving..."), noArgumentsSummary, argumentsSummaryFormat),
            ToolCardKind.Search => new(family, "lucide--search", "text-info", loc.GetOrDefault(ChatTextKeys.ToolCardSearchTitle, "Searching workspace"), subtitle, loc.GetOrDefault(ChatTextKeys.ToolCardSearching, "Searching..."), noArgumentsSummary, argumentsSummaryFormat),
            ToolCardKind.Directory => new(family, "lucide--folder-open", "text-primary", loc.GetOrDefault(ChatTextKeys.ToolCardDirectoryTitle, "Listing directory"), subtitle, loc.GetOrDefault(ChatTextKeys.ToolCardReading, "Reading content..."), noArgumentsSummary, argumentsSummaryFormat),
            ToolCardKind.Document => new(family, "lucide--book-open-text", "text-secondary", loc.GetOrDefault(ChatTextKeys.ToolCardDocumentTitle, "Reading document"), subtitle, loc.GetOrDefault(ChatTextKeys.ToolCardReading, "Reading content..."), noArgumentsSummary, argumentsSummaryFormat),
            ToolCardKind.Memory => new(family, "lucide--hard-drive", "text-secondary", loc.GetOrDefault(ChatTextKeys.ToolCardMemoryTitle, "Using memory"), subtitle, loc.GetOrDefault(ChatTextKeys.ToolCardSearching, "Searching..."), noArgumentsSummary, argumentsSummaryFormat),
            ToolCardKind.Communication => new(family, "lucide--mail", "text-accent", loc.GetOrDefault(ChatTextKeys.ToolCardCommunicationTitle, "Communication tool"), subtitle, loc.GetOrDefault(ChatTextKeys.ToolCardRunning, "Working..."), noArgumentsSummary, argumentsSummaryFormat),
            ToolCardKind.Schedule => new(family, "lucide--calendar", "text-accent", loc.GetOrDefault(ChatTextKeys.ToolCardScheduleTitle, "Calendar tool"), subtitle, loc.GetOrDefault(ChatTextKeys.ToolCardRunning, "Working..."), noArgumentsSummary, argumentsSummaryFormat),
            ToolCardKind.External => new(family, "lucide--globe", "text-info", loc.GetOrDefault(ChatTextKeys.ToolCardExternalTitle, "Online lookup"), subtitle, loc.GetOrDefault(ChatTextKeys.ToolCardSearching, "Searching..."), noArgumentsSummary, argumentsSummaryFormat),
            ToolCardKind.Avatar => new(family, "lucide--bot", "text-secondary", loc.GetOrDefault(ChatTextKeys.ToolCardAvatarTitle, "Avatar action"), subtitle, loc.GetOrDefault(ChatTextKeys.ToolCardRunning, "Working..."), noArgumentsSummary, argumentsSummaryFormat),
            ToolCardKind.Notification => new(family, "lucide--bell", "text-primary", loc.GetOrDefault(ChatTextKeys.ToolCardNotificationTitle, "Notification"), subtitle, loc.GetOrDefault(ChatTextKeys.ToolCardRunning, "Working..."), noArgumentsSummary, argumentsSummaryFormat),
            ToolCardKind.Diagnostics => new(family, "lucide--scroll-text", "text-warning", loc.GetOrDefault(ChatTextKeys.ToolCardDiagnosticsTitle, "Diagnostics"), subtitle, loc.GetOrDefault(ChatTextKeys.ToolCardReading, "Reading content..."), noArgumentsSummary, argumentsSummaryFormat),
            _ => new(family, "lucide--wrench", "text-base-content/70", string.IsNullOrEmpty(tool) ? loc.GetOrDefault(ChatTextKeys.ToolCallLabel, "Tool Call") : tool, subtitle, loc.GetOrDefault(ChatTextKeys.ToolCardRunning, "Working..."), noArgumentsSummary, argumentsSummaryFormat)
        };
    }

    public string BuildSummary(IDictionary<string, object?> arguments)
    {
        string[] preferredKeys = Kind switch
        {
            ToolCardKind.Shell => ["command", "jobId", "job_id"],
            ToolCardKind.FileRead or ToolCardKind.FileWrite => ["file_path", "base_directory"],
            ToolCardKind.SkillSearch => ["query"],
            ToolCardKind.SkillInspect => ["skillPath", "skillName", "repo"],
            ToolCardKind.SkillInstall => ["skillPath", "skillName", "repo"],
            ToolCardKind.SkillAuthoring => ["skillName"],
            ToolCardKind.Search => ["pattern", "query", "path"],
            ToolCardKind.Directory or ToolCardKind.Document => ["path"],
            ToolCardKind.Memory => ["query", "conceptId", "document", "date", "content"],
            ToolCardKind.Communication => ["query", "subject", "messageId", "to"],
            ToolCardKind.Schedule => ["eventId", "timeMin", "timeMax"],
            ToolCardKind.Avatar => ["avatarName", "emotionName", "message", "text"],
            ToolCardKind.Notification => ["message"],
            _ => ["query", "path", "url", "target", "message", "name", "scriptName"]
        };

        foreach (var key in preferredKeys)
        {
            if (arguments.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value?.ToString()))
            {
                // Daily memory notes are often Markdown list items.
                return Kind == ToolCardKind.Memory ? LeadingListMarker().Replace(value.ToString()!, string.Empty) : value.ToString()!;
            }
        }

        // The todo provider's add tool takes {title, description} todos and its complete tool {id, reason} items.
        // A reloaded thread holds redacted lists and dictionaries instead of the original JSON.
        if ((arguments.TryGetValue("todos", out var todos) || arguments.TryGetValue("items", out todos)) && todos is not null &&
            JsonSerializer.SerializeToElement(todos) is { ValueKind: JsonValueKind.Array } items)
        {
            var titles = items.EnumerateArray()
                .Select(static item => item.ValueKind == JsonValueKind.Object &&
                    (item.TryGetProperty("title", out var title) || item.TryGetProperty("reason", out title)) ? title.ToString() : string.Empty)
                .Where(static title => !string.IsNullOrWhiteSpace(title));
            var summary = string.Join(", ", titles);
            if (summary.Length > 0) { return summary; }
        }

        return arguments.Count == 0
            ? NoArgumentsSummary
            : string.Format(CultureInfo.CurrentCulture, ArgumentsSummaryFormat, arguments.Count);
    }

    private static ToolCardKind ResolveFamily(string owner, string tool)
    {
        if (owner == ToolNaming.GetToolsetOwner(typeof(EncryptedFileToolset))) { owner = ToolNaming.GetToolsetOwner(typeof(CodingToolset)); }

        if (!string.IsNullOrEmpty(tool) && BuiltInKinds.TryGetValue(ToolNaming.BuildQualifiedName(owner, tool), out var kind)) { return kind; }

        if (tool == "request_approval") { return ToolCardKind.Approval; }

        if (owner == "memory") { return ToolCardKind.Memory; }

        if (owner is "gmail" or "outlook") { return ToolCardKind.Communication; }

        if (owner is "googlecalendar" or "microsofttodo") { return ToolCardKind.Schedule; }
        // The hosted Avatar MCP server keeps its avatar_* method names.
        if (owner == "avatar" || tool.StartsWith("avatar_", StringComparison.Ordinal)) { return ToolCardKind.Avatar; }

        if (owner is "web" or "news" or "youtube") { return ToolCardKind.External; }

        return ToolCardKind.Generic;
    }

    [GeneratedRegex(@"^\s*[-*+]\s+", RegexOptions.None, 1000)]
    private static partial Regex LeadingListMarker();

    // Keep the owner so shared verbs retain distinct categories across plugin origins.
    private static string ToolName<TToolset>(string methodName) =>
        ToolNaming.BuildToolsetQualifiedName<TToolset>(methodName);
}
