using System.ComponentModel;
using Microsoft.Extensions.AI;
using Oscova.Agents.Collections;
using Oscova.Agents.Policies;

namespace Oscova.Agents.Tests.Framework.Collections;

/// <summary>Checks atomic function decoration and retained tool policy metadata.</summary>
public sealed class AgentToolCollectionTests
{
    [Fact]
    public void Decorate_PreservesPolicyMetadata_AndRejectsRenamingAtomically()
    {
        var tools = new AgentToolCollection();
        tools.Add(new PolicyTools());
        var original = Assert.Single(tools);
        Assert.Equal(ToolApprovalMode.RequireApproval, tools.GetDefaultApprovalMode(original.Name));
        Assert.Equal(NetworkScope.Egress, tools.GetDeclaredNetworkScope(original.Name));
        Assert.True(tools.TrySetNetworkScope(original.Name, NetworkScope.None));
        tools.Decorate(function => new Passthrough(function));
        var decorated = Assert.Single(tools);
        Assert.NotSame(original, decorated);
        Assert.Same(decorated, tools.Find(original.Name));
        Assert.Equal(ToolApprovalMode.RequireApproval, tools.GetDefaultApprovalMode(original.Name));
        Assert.Equal(NetworkScope.Egress, tools.GetDeclaredNetworkScope(original.Name));
        Assert.Equal(NetworkScope.None, tools.GetNetworkScope(original.Name));
        tools.Add(AIFunctionFactory.Create(() => "local", new AIFunctionFactoryOptions { Name = "fixture__local" }));
        var snapshot = tools.ToArray();
        Assert.Throws<InvalidOperationException>(() => tools.Decorate(function => function.Name == original.Name
            ? new Passthrough(function)
            : AIFunctionFactory.Create(() => "renamed", new AIFunctionFactoryOptions { Name = "fixture__renamed" })));
        Assert.Equal(snapshot, tools.ToArray());
        foreach (var function in snapshot) { Assert.Same(function, tools.Find(function.Name)); }
    }

    private sealed class Passthrough(AIFunction inner) : AIFunction
    {
        public override string Name => inner.Name;
        public override string Description => inner.Description;
        public override System.Text.Json.JsonElement JsonSchema => inner.JsonSchema;
        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            inner.InvokeAsync(arguments, cancellationToken);
    }

    private sealed class PolicyTools
    {
        [AgentTool, Description("Returns a fixture value.")]
        [ToolPolicy(ApprovalMode = ToolApprovalMode.RequireApproval), ToolNetworkScope(NetworkScope.Egress)]
        public static string Read() => "fixture";
    }
}
