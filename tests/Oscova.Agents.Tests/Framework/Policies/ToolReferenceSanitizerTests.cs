using Oscova.Agents.Policies;

namespace Oscova.Agents.Tests.Framework.Policies;

public sealed class ToolReferenceSanitizerTests
{
    [Fact]
    public void RemoveUnavailableTools_RemovesStaleAllowedPinnedAndPolicyNames()
    {
        var allowedToolNames = new List<string> { " coding__read_file ", "backgroundshell__kill_command" };
        var pinnedToolNames = new List<string> { " coding__read_file ", "backgroundshell__kill_command", "coding__write_file" };
        var toolPolicies = new List<AgentToolPolicy>
        {
            new() { ToolName = " coding__read_file ", ApprovalMode = ToolApprovalMode.RequireApproval },
            new() { ToolName = "backgroundshell__kill_command", ApprovalMode = ToolApprovalMode.RequireApproval },
            new() { ToolName = "coding__write_file", ApprovalMode = ToolApprovalMode.RequireApproval }
        };
        var availableToolNames = new HashSet<string>(["coding__read_file", "coding__write_file"], StringComparer.OrdinalIgnoreCase);

        var result = ToolReferenceSanitizer.RemoveUnavailableTools(
            allowedToolNames,
            pinnedToolNames,
            toolPolicies,
            availableToolNames);

        Assert.True(result.HasChanges);
        Assert.Equal(["backgroundshell__kill_command"], result.RemovedAllowedToolNames);
        Assert.Equal(["backgroundshell__kill_command", "coding__write_file"], result.RemovedPinnedToolNames);
        Assert.Equal(["backgroundshell__kill_command", "coding__write_file"], result.RemovedPolicyToolNames);
        Assert.Equal(["coding__read_file"], allowedToolNames);
        Assert.Equal(["coding__read_file"], pinnedToolNames);
        var policy = Assert.Single(toolPolicies);
        Assert.Equal("coding__read_file", policy.ToolName);
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
