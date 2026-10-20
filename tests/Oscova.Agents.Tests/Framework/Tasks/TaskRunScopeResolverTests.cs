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
            AllowedToolNames = ["file__read", "memory__search", "search_tools", "library__search"],
            PinnedToolNames = ["file__read", "memory__search", "search_tools", "library__get_index_status"],
            ToolPolicies =
            [
                new AgentToolPolicy { ToolName = "file__read", ApprovalMode = ToolApprovalMode.RequireApproval },
                new AgentToolPolicy { ToolName = "memory__search", ApprovalMode = ToolApprovalMode.RequireApproval },
                new AgentToolPolicy { ToolName = "search_tools", ApprovalMode = ToolApprovalMode.RequireApproval },
                new AgentToolPolicy { ToolName = "library__search", ApprovalMode = ToolApprovalMode.RequireApproval }
            ],
            IsMemoryEnabled = true,
            IsToolSearchEnabled = true
        };

        var scope = TaskRunScope.FromAgent(selectedAgent);

        Assert.Equal(["file__read", "memory__search"], scope.AllowedToolNames);
        Assert.Equal(["file__read", "memory__search"], scope.PinnedToolNames);
        Assert.Equal(["file__read", "memory__search"], scope.ToolPolicies.Select(policy => policy.ToolName));
        Assert.True(scope.IsMemoryEnabled);
        Assert.True(scope.IsToolSearchEnabled);
    }

    [Fact]
    public void ResolveOrThrow_WithLibraryToolInAllowedNames_RejectsDerivedCapability()
    {
        var scope = new TaskRunScope
        {
            AllowedToolNames = ["library__search"]
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
            AllowedToolNames = ["file__read", "shell__exec"],
            InstructionSource = "templates:agent",
            InstructionMode = "append",
            DefaultInstruction = "Supplied default",
            ToolPolicies = [new AgentToolPolicy { ToolName = "shell__exec", ApprovalMode = ToolApprovalMode.Deny }],
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
            AllowedToolNames = ["file__read"],
            ToolPolicies = [new AgentToolPolicy { ToolName = "file__read", ApprovalMode = ToolApprovalMode.RequireApproval }],
            IsToolSearchEnabled = true,
            PinnedToolNames = ["file__read"],
            IsMemoryEnabled = false,
            IsTodoEnabled = false,
            AllowedWorkspaces = [],
            AllowedDirectories =
            [
                new AllowedDirectory("agents/main/private/notes", "notes", DirectorySourceKind.UserDirectory, WorkspaceAccessMode.ReadWrite)
            ]
        };
        var resolver = new TaskRunScopeResolver();

        var surface = resolver.ResolveOrThrow(
            scope,
            selectedAgent,
            effectiveAgent => [.. effectiveAgent.AllowedDirectories]);

        Assert.True(surface.IsScopeApplied);
        Assert.Equal(selectedAgent.Id, surface.EffectiveAgent.Id);
        Assert.Equal(selectedAgent.InstructionSource, surface.EffectiveAgent.InstructionSource);
        Assert.Equal(selectedAgent.InstructionMode, surface.EffectiveAgent.InstructionMode);
        Assert.Equal(selectedAgent.DefaultInstruction, surface.EffectiveAgent.DefaultInstruction);
        Assert.Equal(["file__read"], surface.EffectiveAgent.AllowedToolNames);
        Assert.False(surface.EffectiveAgent.IsMemoryEnabled);
        Assert.False(surface.EffectiveAgent.IsTodoEnabled);
        Assert.False(surface.EffectiveAgent.IsSubagentDelegationEnabled);
        Assert.True(surface.EffectiveAgent.IsToolSearchEnabled);
        Assert.Empty(surface.EffectiveAgent.AllowedWorkspaces);
        var policy = Assert.Single(surface.EffectiveAgent.ToolPolicies);
        Assert.Equal(ToolApprovalMode.RequireApproval, policy.ApprovalMode);
        var directory = Assert.Single(surface.Directories);
        Assert.Equal(DirectorySourceKind.UserDirectory, directory.Kind);

        Assert.Equal(["file__read", "shell__exec"], selectedAgent.AllowedToolNames);
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
            AllowedToolNames = ["file__read"],
            PinnedToolNames = ["file__write"]
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
        const string allowedTaskTool = "file__read";
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
