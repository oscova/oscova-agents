using Oscova.Agents.Core;
using Oscova.Agents.Tasks;
using Oscova.Agents.Infrastructure.Toolsets;
using Oscova.Agents.Toolsets;

namespace Oscova.Agents.Infrastructure.Tasks;

/// <summary>
/// Builds the app-owned task run scopes used by reserved maintenance tasks.
/// </summary>
internal static class ReservedTaskRunScopes
{
    public static TaskRunScope DailyMemory() => MemoryEnabled(
        [ToolNaming.BuildToolsetQualifiedName<MemoryToolset>(nameof(MemoryToolset.SaveDailyMemoryAsync))]);

    public static TaskRunScope Dreaming() => MemoryEnabled(
    [
        ToolNaming.BuildToolsetQualifiedName<MemoryToolset>(nameof(MemoryToolset.ReadMemoryAsync)),
        ToolNaming.BuildToolsetQualifiedName<MemoryToolset>(nameof(MemoryToolset.FindMemoryAsync)),
        ToolNaming.BuildToolsetQualifiedName<MemoryToolset>(nameof(MemoryToolset.ReadDailyMemoryAsync)),
        ToolNaming.BuildToolsetQualifiedName<MemoryToolset>(nameof(MemoryToolset.SaveMemoryAsync))
    ]);

    public static TaskRunScope NightShift() => MemoryEnabled(
    [
        ToolNaming.BuildToolsetQualifiedName<BacklogToolset>(nameof(BacklogToolset.ListAsync)),
        ToolNaming.BuildToolsetQualifiedName<BacklogToolset>(nameof(BacklogToolset.CompleteAsync)),
        ToolNaming.BuildToolsetQualifiedName<BacklogToolset>(nameof(BacklogToolset.SaveAsync)),
        ToolNaming.BuildToolsetQualifiedName<MemoryToolset>(nameof(MemoryToolset.ReadMemoryAsync)),
        ToolNaming.BuildToolsetQualifiedName<MemoryToolset>(nameof(MemoryToolset.FindMemoryAsync)),
        ToolNaming.BuildToolsetQualifiedName<CodingToolset>(nameof(CodingToolset.ReadFile)),
        ToolNaming.BuildToolsetQualifiedName<CodingToolset>(nameof(CodingToolset.WriteFile)),
        ToolNaming.BuildToolsetQualifiedName<CodingToolset>(nameof(CodingToolset.EditFile)),
        ToolNaming.BuildToolsetQualifiedName<CodingToolset>(nameof(CodingToolset.ApplyPatch)),
        ToolNaming.BuildToolsetQualifiedName<CodingToolset>(nameof(CodingToolset.Grep)),
        ToolNaming.BuildToolsetQualifiedName<CodingToolset>(nameof(CodingToolset.Glob)),
        ToolNaming.BuildToolsetQualifiedName<DirectoryToolset>(nameof(DirectoryToolset.ListDirectory)),
        ToolNaming.BuildToolsetQualifiedName<ShellToolset>(nameof(ShellToolset.ExecCommandAsync)),
        ToolNaming.BuildToolsetQualifiedName<ShellToolset>(nameof(ShellToolset.StartCommandAsync)),
        ToolNaming.BuildToolsetQualifiedName<ShellToolset>(nameof(ShellToolset.ReadCommandOutputAsync)),
        ToolNaming.BuildToolsetQualifiedName<ShellToolset>(nameof(ShellToolset.KillCommandAsync))
    ], [UserFileNames.DefaultWorkspaceDirectory, UserFileNames.ToolsWorkspaceDirectory]);

    public static TaskRunScope MorningBrief() => MemoryEnabled(
    [
        ToolNaming.BuildToolsetQualifiedName<BacklogToolset>(nameof(BacklogToolset.ListAsync)),
        ToolNaming.BuildToolsetQualifiedName<NotificationToolset>(nameof(NotificationToolset.NotifyUserAsync)),
        ToolNaming.BuildToolsetQualifiedName<AgentTaskToolset>(nameof(AgentTaskToolset.InspectTaskAsync)),
        ToolNaming.BuildToolsetQualifiedName<AgentTaskToolset>(nameof(AgentTaskToolset.ReadRunAsync)),
        ToolNaming.BuildToolsetQualifiedName<MemoryToolset>(nameof(MemoryToolset.ReadMemoryAsync))
    ]);

    private static TaskRunScope MemoryEnabled(IReadOnlyList<string> tools, IReadOnlyList<string>? workspaces = null)
    {
        return new TaskRunScope
        {
            AllowedToolNames = [.. tools],
            ToolPolicies = [],
            IsToolSearchEnabled = false,
            PinnedToolNames = [],
            IsCompactionEnabled = false,
            IsMemoryEnabled = true,
            IsTodoEnabled = false,
            IsSubagentDelegationEnabled = false,
            IsLocalOnly = false,
            AgentToolIds = [],
            AllowedSkillNames = [],
            AllowedWorkspaces = [.. workspaces ?? []],
            AllowedDirectories = []
        };
    }

    public static TaskRunScope Reflection()
    {
        return new TaskRunScope
        {
            AllowedToolNames =
            [
                ToolNaming.BuildToolsetQualifiedName<NotesToolset>(nameof(NotesToolset.ReadNotesAsync)),
                ToolNaming.BuildToolsetQualifiedName<NotesToolset>(nameof(NotesToolset.FindNotesAsync)),
                ToolNaming.BuildToolsetQualifiedName<NotesToolset>(nameof(NotesToolset.SaveNotesAsync))
            ],
            ToolPolicies = [],
            IsToolSearchEnabled = false,
            PinnedToolNames = [],
            IsCompactionEnabled = false,
            IsMemoryEnabled = false,
            IsTodoEnabled = false,
            IsSubagentDelegationEnabled = false,
            IsLocalOnly = false,
            AgentToolIds = [],
            AllowedSkillNames = [],
            AllowedWorkspaces = [],
            AllowedDirectories = []
        };
    }
}
