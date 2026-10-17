using System.Reflection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Oscova.Agents.Collections;
using Oscova.Agents.Policies;
using Oscova.Agents.Providers;
using Oscova.Agents.Tests.Framework.TestTools;

namespace Oscova.Agents.Tests.Framework.Providers;

public sealed class ToolSearchProviderTests
{

    /// <summary>Empty discovery instructions remain exact while the search tool stays available.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(" \t\n")]
    public async Task ProvideAIContextAsync_WithEmptyInstructions_KeepsEmptyInstructions(string instructions)
    {
        var tools = new AgentToolCollection();
        var provider = new ToolSearchProvider([], [], tools, new AgentToolResolver(tools), new LexicalToolSearchStrategy(), instructions);
        var context = await InvokeProviderAsync(provider);
        Assert.Equal(instructions, context.Instructions);
        Assert.Equal("search_tools", Assert.Single(context.Tools!).Name);
    }

    /// <summary>Inspection exposes only unpinned functions permitted by policy and offline mode.</summary>
    [Fact]
    public void GetSearchableTools_RespectsPinsPoliciesAndEgress()
    {
        var pinned = AIFunctionFactory.Create(() => true, "pinned");
        var unpinned = AIFunctionFactory.Create(() => true, "unpinned");
        var denied = AIFunctionFactory.Create(() => true, "denied");
        var egress = AIFunctionFactory.Create(() => true, "egress");
        var tools = new AgentToolCollection { pinned, unpinned, denied };
        tools.Add(egress, "external", egress.Name, NetworkScope.Egress);
        var resolver = new AgentToolResolver(tools,
            [new AgentToolPolicy { ToolName = denied.Name, ApprovalMode = ToolApprovalMode.Deny }],
            airGapped: true);
        var provider = new ToolSearchProvider(
            tools.Select(tool => tool.Name), [pinned.Name], tools, resolver, new LexicalToolSearchStrategy());

        Assert.Same(unpinned, Assert.Single(provider.GetSearchableTools()));
    }

    [Fact]
    public async Task ProvideAIContextAsync_ReturnsOnlySearchToolBeforeUnlock()
    {
        var provider = CreateProvider(out _);

        var context = await InvokeProviderAsync(provider);

        var tool = Assert.Single(context.Tools!);
        Assert.Equal("search_tools", tool.Name);
        Assert.Contains("hidden behind `search_tools`", context.Instructions);
    }

    [Fact]
    public async Task ProvideAIContextAsync_ResurfacesUnlockedToolAfterSearch()
    {
        var provider = CreateProvider(out string playSongName);

        var searchResult = InvokeSearchTools(provider, "play song");
        var context = await InvokeProviderAsync(provider);

        Assert.Contains(playSongName, searchResult);
        Assert.Contains(context.Tools!, tool => tool.Name == "search_tools");
        Assert.Contains(context.Tools!, tool => tool.Name == playSongName);
    }

    [Fact]
    public async Task ProvideAIContextAsync_PinnedToolIsExposedAndExcludedFromSearch()
    {
        var qualifier = ToolNaming.GetToolsetOwner(typeof(SimpleTestTool));
        var pinnedToolName = ToolNaming.BuildQualifiedName(qualifier, "PlaySong");
        var pinnedProvider = CreateProvider(out string playSongName, out string turnOnMusicName, pinnedToolName);

        var context = await InvokeProviderAsync(pinnedProvider);
        var searchResult = InvokeSearchTools(pinnedProvider, "play song");
        var unpinnedSearchResult = InvokeSearchTools(pinnedProvider, "embedded music player");

        Assert.Contains(context.Tools!, tool => tool.Name == "search_tools");
        Assert.Contains(context.Tools!, tool => tool.Name == playSongName);
        Assert.DoesNotContain(playSongName, searchResult);
        Assert.Contains(turnOnMusicName, unpinnedSearchResult);
    }

    private static ToolSearchProvider CreateProvider(out string playSongName, string? pinnedToolName = null)
    {
        return CreateProvider(out playSongName, out _, pinnedToolName);
    }

    private static ToolSearchProvider CreateProvider(
        out string playSongName,
        out string turnOnMusicName,
        string? pinnedToolName = null)
    {
        var collection = new AgentToolCollection
        {
            new SimpleTestTool()
        };
        var qualifier = ToolNaming.GetToolsetOwner(typeof(SimpleTestTool));
        var allowedToolNames = collection.Select(tool => tool.Name).ToList();
        playSongName = ToolNaming.BuildQualifiedName(qualifier, "PlaySong");
        turnOnMusicName = ToolNaming.BuildQualifiedName(qualifier, "TurnOnMusic");
        return new ToolSearchProvider(
            allowedToolNames,
            pinnedToolName is null ? [] : [pinnedToolName],
            collection,
            new AgentToolResolver(collection),
            new LexicalToolSearchStrategy());
    }

    private static string InvokeSearchTools(ToolSearchProvider provider, string query)
    {
        var method = typeof(ToolSearchProvider).GetMethod(
            "SearchTools",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        return Assert.IsType<string>(method.Invoke(provider, [query]));
    }

    private static async Task<AIContext> InvokeProviderAsync(AIContextProvider provider)
    {
        var method = typeof(ToolSearchProvider).GetMethod(
            "ProvideAIContextAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var result = method.Invoke(provider, [null!, CancellationToken.None]);
        var contextTask = Assert.IsType<ValueTask<AIContext>>(result);
        return await contextTask.ConfigureAwait(false);
    }
}
