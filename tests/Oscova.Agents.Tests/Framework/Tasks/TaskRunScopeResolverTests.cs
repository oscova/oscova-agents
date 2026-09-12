using Oscova.Agents.Policies;
using Oscova.Agents.Tasks;
using Oscova.Agents.Toolsets;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Tests.Framework.Tasks;

public sealed class TaskRunScopeResolverTests
{
    [Fact]
    public void FromAgent_DoesNotCopyToolsControlledByDedicatedScopeFlags()
    {
        var selectedAgent = new AgentDefinition
        {
            Id = "agent-1",
            Name = "Agent",
            AllowedToolNames = ["coding__read_file", "memory_search", "search_tools", "library_search"],
            PinnedToolNames = ["coding__read_file", "memory_search", "search_tools", "library_index_status"],
            ToolPolicies =
            [
                new AgentToolPolicy { ToolName = "coding__read_file", ApprovalMode = ToolApprovalMode.RequireApproval },
                new AgentToolPolicy { ToolName = "memory_search", ApprovalMode = ToolApprovalMode.RequireApproval },
                new AgentToolPolicy { ToolName = "search_tools", ApprovalMode = ToolApprovalMode.RequireApproval },
                new AgentToolPolicy { ToolName = "library_search", ApprovalMode = ToolApprovalMode.RequireApproval }
            ],
            IsMemoryEnabled = true,
            IsToolSearchEnabled = true
        };

        var scope = TaskRunScope.FromAgent(selectedAgent);

        Assert.Equal(["coding__read_file"], scope.AllowedToolNames);
        Assert.Equal(["coding__read_file"], scope.PinnedToolNames);
        var policy = Assert.Single(scope.ToolPolicies);
        Assert.Equal("coding__read_file", policy.ToolName);
        Assert.True(scope.IsMemoryEnabled);
        Assert.True(scope.IsToolSearchEnabled);
    }

    [Fact]
    public void ResolveOrThrow_WithLibraryToolInAllowedNames_RejectsDerivedCapability()
    {
        var scope = new TaskRunScope
        {
            AllowedToolNames = ["library_search"]
        };
        var resolver = new TaskRunScopeResolver();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            resolver.ResolveOrThrow(
                scope,
                new AgentDefinition { Id = "agent-1" },
                _ => []));

        Assert.Contains("Library workspace grant", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveOrThrow_WithCustomScope_ReplacesRuntimeSurfaceWithoutMutatingAgent()
    {
        var selectedAgent = new AgentDefinition
        {
            Id = "agent-1",
            Name = "Agent",
            AllowedToolNames = ["coding__read_file", "shell__exec_command"],
            ToolPolicies = [new AgentToolPolicy { ToolName = "shell__exec_command", ApprovalMode = ToolApprovalMode.Deny }],
            IsMemoryEnabled = true,
            IsTodoEnabled = true,
            IsToolSearchEnabled = false,
            AllowedWorkspaces = ["default"],
            AllowedDirectories =
            [
                new AllowedDirectory("C:/old", "old", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadOnly)
            ]
        };
        var scope = new TaskRunScope
        {
            AllowedToolNames = ["coding__read_file"],
            ToolPolicies = [new AgentToolPolicy { ToolName = "coding__read_file", ApprovalMode = ToolApprovalMode.RequireApproval }],
            IsToolSearchEnabled = true,
            PinnedToolNames = ["coding__read_file"],
            IsMemoryEnabled = false,
            IsTodoEnabled = false,
            AllowedWorkspaces = [],
            AllowedDirectories =
            [
                new AllowedDirectory("agents/main/data/notes", "notes", DirectorySourceKind.UserDirectory, WorkspaceAccessMode.ReadWrite)
            ]
        };
        var resolver = new TaskRunScopeResolver();

        var surface = resolver.ResolveOrThrow(
            scope,
            selectedAgent,
            effectiveAgent => [.. effectiveAgent.AllowedDirectories]);

        Assert.True(surface.IsScopeApplied);
        Assert.Equal(selectedAgent.Id, surface.EffectiveAgent.Id);
        Assert.Equal(["coding__read_file"], surface.EffectiveAgent.AllowedToolNames);
        Assert.False(surface.EffectiveAgent.IsMemoryEnabled);
        Assert.False(surface.EffectiveAgent.IsTodoEnabled);
        Assert.True(surface.EffectiveAgent.IsToolSearchEnabled);
        Assert.Empty(surface.EffectiveAgent.AllowedWorkspaces);
        var policy = Assert.Single(surface.EffectiveAgent.ToolPolicies);
        Assert.Equal(ToolApprovalMode.RequireApproval, policy.ApprovalMode);
        var directory = Assert.Single(surface.Directories);
        Assert.Equal(DirectorySourceKind.UserDirectory, directory.Kind);

        Assert.Equal(["coding__read_file", "shell__exec_command"], selectedAgent.AllowedToolNames);
        Assert.True(selectedAgent.IsMemoryEnabled);
        Assert.True(selectedAgent.IsTodoEnabled);
        Assert.Equal(["default"], selectedAgent.AllowedWorkspaces);
    }

    [Fact]
    public void TryResolve_WhenPinnedToolIsNotSelected_ReturnsValidationError()
    {
        var selectedAgent = new AgentDefinition { Id = "agent-1" };
        var scope = new TaskRunScope
        {
            AllowedToolNames = ["coding__read_file"],
            PinnedToolNames = ["coding__write_file"]
        };
        var resolver = new TaskRunScopeResolver();

        var resolved = resolver.TryResolve(
            scope,
            selectedAgent,
            _ => [],
            out _,
            out var error);

        Assert.False(resolved);
        Assert.Contains("Pinned tool", error);
    }

    [Fact]
    public void Resolve_ImplicitAgentScope_RemovesToolsUnavailableToTasks()
    {
        const string allowedTaskTool = "coding__read_file";
        var selectedAgent = new AgentDefinition
        {
            Id = "agent-1",
            AllowedToolNames = [allowedTaskTool, HostShellToolset.QualifiedToolName],
            PinnedToolNames = [allowedTaskTool, HostShellToolset.QualifiedToolName],
            ToolPolicies =
            [
                new AgentToolPolicy
                {
                    ToolName = HostShellToolset.QualifiedToolName,
                    ApprovalMode = ToolApprovalMode.AutoApprove
                }
            ]
        };
        var catalog = new TaskRunScopeValidationCatalog
        {
            ToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { allowedTaskTool }
        };

        TaskRunSurface surface = new TaskRunScopeResolver().ResolveOrThrow(
            scope: null,
            selectedAgent,
            _ => [],
            catalog);

        Assert.False(surface.IsScopeApplied);
        Assert.Equal([allowedTaskTool], surface.EffectiveAgent.AllowedToolNames);
        Assert.Equal([allowedTaskTool], surface.EffectiveAgent.PinnedToolNames);
        Assert.Empty(surface.EffectiveAgent.ToolPolicies);
        Assert.Contains(HostShellToolset.QualifiedToolName, selectedAgent.AllowedToolNames);
    }
}
