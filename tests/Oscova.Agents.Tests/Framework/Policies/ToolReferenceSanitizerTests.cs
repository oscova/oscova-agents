using Oscova.Agents.Policies;

namespace Oscova.Agents.Tests.Framework.Policies;

public sealed class ToolReferenceSanitizerTests
{
    [Fact]
    public void RemoveUnavailableTools_RemovesStaleAllowedPinnedAndPolicyNames()
    {
        var allowedToolNames = new List<string> { " file__read ", "backgroundshell__kill" };
        var pinnedToolNames = new List<string> { " file__read ", "backgroundshell__kill", "file__write" };
        var toolPolicies = new List<AgentToolPolicy>
        {
            new() { ToolName = " file__read ", ApprovalMode = ToolApprovalMode.RequireApproval },
            new() { ToolName = "backgroundshell__kill", ApprovalMode = ToolApprovalMode.RequireApproval },
            new() { ToolName = "file__write", ApprovalMode = ToolApprovalMode.RequireApproval }
        };
        var availableToolNames = new HashSet<string>(["file__read", "file__write"], StringComparer.OrdinalIgnoreCase);

        var result = ToolReferenceSanitizer.RemoveUnavailableTools(
            allowedToolNames,
            pinnedToolNames,
            toolPolicies,
            availableToolNames);

        Assert.True(result.HasChanges);
        Assert.Equal(["backgroundshell__kill"], result.RemovedAllowedToolNames);
        Assert.Equal(["backgroundshell__kill", "file__write"], result.RemovedPinnedToolNames);
        Assert.Equal(["backgroundshell__kill", "file__write"], result.RemovedPolicyToolNames);
        Assert.Equal(["file__read"], allowedToolNames);
        Assert.Equal(["file__read"], pinnedToolNames);
        var policy = Assert.Single(toolPolicies);
        Assert.Equal("file__read", policy.ToolName);
    }

    [Fact]
    public void RemoveUnavailableTools_WhenCatalogIsMissing_LeavesReferencesUnchanged()
    {
        var allowedToolNames = new List<string> { "missing__tool" };
        var pinnedToolNames = new List<string> { "missing__tool" };
        var toolPolicies = new List<AgentToolPolicy>
        {
            new() { ToolName = "missing__tool", ApprovalMode = ToolApprovalMode.RequireApproval }
        };

        var result = ToolReferenceSanitizer.RemoveUnavailableTools(
            allowedToolNames,
            pinnedToolNames,
            toolPolicies,
            availableToolNames: null);

        Assert.False(result.HasChanges);
        Assert.Equal(["missing__tool"], allowedToolNames);
        Assert.Equal(["missing__tool"], pinnedToolNames);
        var policy = Assert.Single(toolPolicies);
        Assert.Equal("missing__tool", policy.ToolName);
    }

    [Fact]
    public void RemoveUnavailableNames_RemovesStaleAgentToolAndNormalizesAvailableAgentTool()
    {
        var agentToolIds = new List<string> { "reflection", "available-agent " };
        var exposedAgentIds = new HashSet<string>(["available-agent"], StringComparer.OrdinalIgnoreCase);

        var removed = ToolReferenceSanitizer.RemoveUnavailableNames(agentToolIds, exposedAgentIds);

        Assert.Equal(["reflection"], removed);
        Assert.Equal(["available-agent"], agentToolIds);
    }
}
