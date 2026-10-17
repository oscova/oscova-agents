using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Oscova.Agents.Collections;
using Oscova.Agents.Policies;
using Oscova.Agents.Providers;
using Oscova.Agents.Tests.Framework.TestTools;
using Oscova.Agents.Toolsets;
using System.ComponentModel;
using System.Reflection;

namespace Oscova.Agents.Tests.Framework.Providers;

public sealed class AgentToolProviderTests
{
    private readonly AgentToolCollection _collection;
    private readonly string _addToolName;
    private readonly string _riskyToolName;

    public AgentToolProviderTests()
    {
        _collection = new AgentToolCollection();
        _collection.Add(new SimpleTestTool());
        var qualifier = ToolNaming.GetToolsetOwner(typeof(SimpleTestTool));
        _addToolName = ToolNaming.BuildQualifiedName(qualifier, "Add");
        _riskyToolName = ToolNaming.BuildQualifiedName(qualifier, "risky");
    }

    [Fact]
    public async Task ProvideAIContextAsync_WithRequireApproval_WrapsTool()
    {
        var provider = new AgentToolProvider(
            [_addToolName],
            _collection,
            [new AgentToolPolicy
            {
                ToolName = _addToolName,
                ApprovalMode = ToolApprovalMode.RequireApproval
            }]);

        var context = await InvokeProviderAsync(provider);

        var tool = Assert.Single(context.Tools!);
        Assert.IsType<ApprovalRequiredAIFunction>(tool);
    }

    [Fact]
    public async Task ProvideAIContextAsync_WithDeny_OmitsTool()
    {
        var provider = new AgentToolProvider(
            [_addToolName],
            _collection,
            [new AgentToolPolicy
            {
                ToolName = _addToolName,
                ApprovalMode = ToolApprovalMode.Deny
            }]);

        var context = await InvokeProviderAsync(provider);

        Assert.Empty(context.Tools!);
    }

    [Fact]
    public async Task ProvideAIContextAsync_WithDefault_ExposesRawTool()
    {
        var provider = new AgentToolProvider([_addToolName], _collection);

        var context = await InvokeProviderAsync(provider);

        var tool = Assert.Single(context.Tools!);
        // Raw tool is a framework reflection wrapper, not an ApprovalRequiredAIFunction.
        Assert.IsType<AIFunction>(tool, exactMatch: false);
        Assert.IsNotType<ApprovalRequiredAIFunction>(tool);
        Assert.Equal(_addToolName, tool.Name);
    }

    [Fact]
    public async Task ProvideAIContextAsync_WithAutoApprove_ExposesRawTool()
    {
        var provider = new AgentToolProvider(
            [_addToolName],
            _collection,
            [new AgentToolPolicy
            {
                ToolName = _addToolName,
                ApprovalMode = ToolApprovalMode.AutoApprove
            }]);

        var context = await InvokeProviderAsync(provider);

        var tool = Assert.Single(context.Tools!);
        Assert.IsNotType<ApprovalRequiredAIFunction>(tool);
    }

    [Fact]
    public async Task ProvideAIContextAsync_BuiltInRequireApproval_WrapsToolByDefault()
    {
        var provider = new AgentToolProvider([_riskyToolName], _collection);

        var context = await InvokeProviderAsync(provider);

        var tool = Assert.Single(context.Tools!);
        Assert.IsType<ApprovalRequiredAIFunction>(tool);
    }

    [Fact]
    public async Task ProvideAIContextAsync_BuiltInRequireApproval_OverriddenByAutoApprove_ExposesRawTool()
    {
        var provider = new AgentToolProvider(
            [_riskyToolName],
            _collection,
            [new AgentToolPolicy
            {
                ToolName = _riskyToolName,
                ApprovalMode = ToolApprovalMode.AutoApprove
            }]);

        var context = await InvokeProviderAsync(provider);

        var tool = Assert.Single(context.Tools!);
        Assert.IsNotType<ApprovalRequiredAIFunction>(tool);
    }

    [Fact]
    public async Task ProvideAIContextAsync_BuiltInRequireApproval_OverriddenByDeny_OmitsTool()
    {
        var provider = new AgentToolProvider(
            [_riskyToolName],
            _collection,
            [new AgentToolPolicy
            {
                ToolName = _riskyToolName,
                ApprovalMode = ToolApprovalMode.Deny
            }]);

        var context = await InvokeProviderAsync(provider);

        Assert.Empty(context.Tools!);
    }

    [Theory]
    [InlineData(ToolApprovalMode.Default)]
    [InlineData(ToolApprovalMode.RequireApproval)]
    [InlineData(ToolApprovalMode.Deny)]
    public async Task ProvideAIContextAsync_FileWritesAutoApproveUnlessExplicitlyRestricted(ToolApprovalMode mode)
    {
        var collection = new AgentToolCollection { new CodingToolset() };
        var names = new[] { nameof(CodingToolset.WriteFile), nameof(CodingToolset.EditFile) }
            .Select(name => ToolNaming.BuildToolsetQualifiedName<CodingToolset>(name)).ToArray();
        var tools = collection.Where(tool => names.Contains(tool.Name, StringComparer.Ordinal)).ToArray();
        Assert.Equal(2, tools.Length);
        var provider = new AgentToolProvider(
            tools.Select(tool => tool.Name).ToArray(), collection,
            tools.Select(tool => new AgentToolPolicy { ToolName = tool.Name, ApprovalMode = mode }));
        var context = await InvokeProviderAsync(provider);

        if (mode == ToolApprovalMode.Deny)
        {
            Assert.Empty(context.Tools!);
            return;
        }
        var exposed = context.Tools!.ToArray();
        Assert.Equal(2, exposed.Length);
        foreach (var tool in exposed)
        {
            if (mode == ToolApprovalMode.RequireApproval) { Assert.IsType<ApprovalRequiredAIFunction>(tool); }
            else { Assert.IsNotType<ApprovalRequiredAIFunction>(tool); }
        }
    }

    [Fact]
    public async Task ProvideAIContextAsync_UnknownToolName_IsSkipped()
    {
        var provider = new AgentToolProvider(["nonexistent/tool"], _collection);

        var context = await InvokeProviderAsync(provider);

        Assert.Empty(context.Tools!);
    }

    [Fact]
    public async Task ProvideAIContextAsync_WhenAirGapped_OmitsEgressTools()
    {
        var collection = new AgentToolCollection
        {
            new ScopedToolset()
        };
        var qualifier = ToolNaming.GetToolsetOwner(typeof(ScopedToolset));
        var localToolName = ToolNaming.BuildQualifiedName(qualifier, "local-tool");
        var shellToolName = ToolNaming.BuildQualifiedName(qualifier, "shell-tool");
        var internetToolName = ToolNaming.BuildQualifiedName(qualifier, "internet-tool");
        var provider = new AgentToolProvider(
            [localToolName, shellToolName, internetToolName],
            collection,
            airGapped: true);

        var context = await InvokeProviderAsync(provider);

        Assert.Contains(context.Tools!, tool => tool.Name == localToolName);
        Assert.Contains(context.Tools!, tool => tool.Name == shellToolName);
        Assert.DoesNotContain(context.Tools!, tool => tool.Name == internetToolName);
    }

    [Fact]
    public async Task ProvideAIContextAsync_WhenAirGapped_OmitsExternalToolMarkedEgress()
    {
        var collection = new AgentToolCollection();
        var method = typeof(AgentToolProviderTests).GetMethod(nameof(ExternalToolStub))!;
        var function = AIFunctionFactory.Create(
            method,
            target: null,
            options: new AIFunctionFactoryOptions { Name = "mcp_server__search_web" });
        collection.Add(function, "mcp-server", "search-web");
        Assert.True(collection.TrySetNetworkScope(function.Name, NetworkScope.Egress));
        var provider = new AgentToolProvider([function.Name], collection, airGapped: true);

        var context = await InvokeProviderAsync(provider);

        Assert.Empty(context.Tools!);
    }

    [Fact]
    public async Task ProvideAIContextAsync_ExplicitSelectionExposesEncryptedReader()
    {
        var collection = new AgentToolCollection { new ConfidentialOnlyToolset() };
        var toolName = Assert.Single(collection).Name;
        var context = await InvokeProviderAsync(new AgentToolProvider([toolName], collection));
        Assert.Single(context.Tools!);
    }

    public static string ExternalToolStub()
    {
        return "ok";
    }

    private static async Task<AIContext> InvokeProviderAsync(AgentToolProvider provider)
    {
        var method = typeof(AgentToolProvider).GetMethod(
            "ProvideAIContextAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var result = method.Invoke(provider, [null!, CancellationToken.None]);
        var contextTask = Assert.IsType<ValueTask<AIContext>>(result);
        return await contextTask.ConfigureAwait(false);
    }

    private sealed class ScopedToolset
    {
        [AgentTool]
        [DisplayName("local-tool")]
        [ToolNetworkScope(NetworkScope.None)]
        public static string LocalTool()
        {
            return "ok";
        }

        [AgentTool]
        [DisplayName("shell-tool")]
        [ToolNetworkScope(NetworkScope.Incidental)]
        public static string ShellTool()
        {
            return "ok";
        }

        [AgentTool]
        [DisplayName("internet-tool")]
        [ToolNetworkScope(NetworkScope.Egress)]
        public static string InternetTool()
        {
            return "ok";
        }
    }

    private sealed class ConfidentialOnlyToolset
    {
        [AgentTool]
        public static string ReadSecret() => "secret";
    }
}
