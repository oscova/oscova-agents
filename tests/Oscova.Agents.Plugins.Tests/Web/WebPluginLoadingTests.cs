using Oscova.Agents.Tests.TestSupport.TestPlugin;

namespace Oscova.Agents.Plugins.Tests.Web;

public sealed class WebPluginLoadingTests
{
    [Fact]
    public void LoadDirectory_LoadsDefaultWebPluginToolsets()
    {
        using var pluginRoot = TestPluginRoot.Create();
        pluginRoot.AddPluginFromProjectOutput(
            Path.Combine("src", "Oscova.Agents.Plugins.Web"),
            "oscova.web");

        var catalog = AgentPluginLoader.LoadDirectory(pluginRoot.RootPath);

        Assert.DoesNotContain(catalog.Diagnostics, diagnostic => diagnostic.Level == AgentPluginLoadDiagnosticLevel.Error);

        var plugin = Assert.Single(catalog.Plugins);
        Assert.Equal("oscova.web", plugin.Manifest.Id);
        Assert.Contains(plugin.Toolsets, contribution => contribution.ToolsetType.Name == "WebSearchToolset");
        Assert.Contains(plugin.Toolsets, contribution => contribution.ToolsetType.Name == "NewsToolset");

        using var httpClient = new HttpClient();
        var context = PluginTestContext.Create(new NullToolCredentialResolver(), plugin.PluginDirectory,
            Path.Combine(pluginRoot.RootPath, "models"));

        foreach (var contribution in plugin.Toolsets)
        {
            Assert.IsAssignableFrom<IAgentToolset>(contribution.Factory(context));
        }
    }

    private sealed class NullToolCredentialResolver : IToolCredentialResolver
    {
        public Task<string?> ResolveAsync(string credentialKey, string fieldName, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<string?> ResolveAsync(string credentialKey, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }
}
