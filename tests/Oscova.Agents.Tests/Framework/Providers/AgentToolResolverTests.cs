using System.ComponentModel;
using Microsoft.Extensions.AI;
using Oscova.Agents.Collections;
using Oscova.Agents.Policies;
using Oscova.Agents.Providers;
using Oscova.Agents.Tests.Framework.TestTools;

namespace Oscova.Agents.Tests.Framework.Providers;

public sealed class AgentToolResolverTests
{
    [Fact]
    public void ResolveAll_AppliesGovernanceParityRules()
    {
        var collection = new AgentToolCollection
        {
            new SimpleTestTool(),
            new ScopedToolset()
        };
        var simpleQualifier = ToolNaming.GetToolsetOwner(typeof(SimpleTestTool));
        var scopedQualifier = ToolNaming.GetToolsetOwner(typeof(ScopedToolset));
        var addToolName = ToolNaming.BuildQualifiedName(simpleQualifier, "Add");
        var deniedToolName = ToolNaming.BuildQualifiedName(simpleQualifier, "subtraction");
        var approvalToolName = ToolNaming.BuildQualifiedName(simpleQualifier, "PlaySong");
        var internetToolName = ToolNaming.BuildQualifiedName(scopedQualifier, "internet-tool");
        var resolver = new AgentToolResolver(
            collection,
            [
                new AgentToolPolicy { ToolName = deniedToolName, ApprovalMode = ToolApprovalMode.Deny },
                new AgentToolPolicy { ToolName = approvalToolName, ApprovalMode = ToolApprovalMode.RequireApproval }
            ],
            airGapped: true);

        var result = resolver.ResolveAll([addToolName, deniedToolName, approvalToolName, internetToolName, "missing.tool"]);

        Assert.Contains(result, tool => tool.Name == addToolName);
        Assert.DoesNotContain(result, tool => tool.Name == deniedToolName);
        Assert.DoesNotContain(result, tool => tool.Name == internetToolName);
        Assert.Contains(result, tool => tool is ApprovalRequiredAIFunction && tool.Name == approvalToolName);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ResolveAll_DropsApprovalRequiredTools_WhenSuppressed()
    {
        // Regression: sub-agents run inside AsAIFunction() and never reach ToolApprovalRunLoop;
        // an approval-required tool would emit an unanswerable approval request.
        var collection = new AgentToolCollection
        {
            new SimpleTestTool(),
            new ScopedToolset()
        };
        var simpleQualifier = ToolNaming.GetToolsetOwner(typeof(SimpleTestTool));
        var addToolName = ToolNaming.BuildQualifiedName(simpleQualifier, "Add");
        var approvalToolName = ToolNaming.BuildQualifiedName(simpleQualifier, "PlaySong");
        var resolver = new AgentToolResolver(
            collection,
            [
                new AgentToolPolicy { ToolName = approvalToolName, ApprovalMode = ToolApprovalMode.RequireApproval }
            ],
            suppressApprovalRequiredTools: true);

        var result = resolver.ResolveAll([addToolName, approvalToolName]);

        Assert.Contains(result, tool => tool.Name == addToolName);
        Assert.DoesNotContain(result, tool => tool.Name == approvalToolName);
        Assert.Single(result);
    }

    [Fact]
    public void ResolveAll_AutoApprovedToolRemainsUnwrappedForRunAutoRejectRouting()
    {
        var collection = new AgentToolCollection { new SimpleTestTool() };
        string qualifier = ToolNaming.GetToolsetOwner(typeof(SimpleTestTool));
        string toolName = ToolNaming.BuildQualifiedName(qualifier, "risky");
        var resolver = new AgentToolResolver(
            collection,
            [new AgentToolPolicy { ToolName = toolName, ApprovalMode = ToolApprovalMode.AutoApprove }]);

        AITool tool = Assert.Single(resolver.ResolveAll([toolName]));

        Assert.IsNotType<ApprovalRequiredAIFunction>(tool);
        Assert.Equal(toolName, tool.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolveAll_AppliesConfidentialOnlyMarker(bool isConfidential)
    {
        var collection = new AgentToolCollection
        {
            new ConfidentialOnlyToolset(),
            new PlainToolset()
        };
        var names = collection.Select(tool => tool.Name).ToList();
        var resolver = new AgentToolResolver(collection);

        var result = resolver.ResolveAll(names);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, tool => IsFunctionFor(tool, nameof(ConfidentialOnlyToolset.ReadSecret)));
        Assert.Contains(result, tool => IsFunctionFor(tool, nameof(PlainToolset.ReadMemory)));
    }



    private static bool IsFunctionFor(AITool tool, string methodName) =>
        tool is AIFunction function && function.UnderlyingMethod?.Name == methodName;

    private sealed class ConfidentialOnlyToolset
    {
        [AgentTool]
        public static string ReadSecret() => "secret";
    }

    private sealed class PlainToolset
    {
        [AgentTool]
        public static string ReadMemory() => "memory";
    }

    private sealed class NonReflectedTool : AITool;

    private sealed class ScopedToolset
    {
        [AgentTool]
        [DisplayName("internet-tool")]
        [ToolNetworkScope(NetworkScope.Egress)]
        public static string InternetTool()
        {
            return "ok";
        }
    }
}
