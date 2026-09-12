using Oscova.Agents.Collections;
using Oscova.Agents.Plugins;
using Oscova.Agents.Tests.TestSupport.TestPlugin;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace Oscova.Agents.Tests.Framework.Plugins;

public sealed class AgentPluginLoaderTests
{
    [SkippableFact]
    public void NativeLibraryPath_OnWindows_UsesExtendedPathSyntax()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Extended path syntax is Windows-specific.");

        const string libraryPath = @"C:\plugins\packages\runtime\native.dll";

        Assert.Equal(@"\\?\C:\plugins\packages\runtime\native.dll", AgentPluginLoadContext.GetNativeLibraryPath(libraryPath));
    }

    [Fact]
    public void LoadDirectory_ReturnsEmptyCatalogForMissingRoot()
    {
        var missingRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var catalog = AgentPluginLoader.LoadDirectory(missingRoot);

        Assert.Empty(catalog.Plugins);
        Assert.Empty(catalog.Diagnostics);
    }

    [Fact]
    public void LoadDirectory_LoadsTestPluginAndReflectsToolset()
    {
        using var pluginRoot = TestPluginRoot.Create();
        var pluginDirectory = pluginRoot.AddTestPlugin();
        var modelStorageDirectory = Path.Combine(pluginRoot.RootPath, "models");

        var catalog = AgentPluginLoader.LoadDirectory(pluginRoot.RootPath);

        Assert.DoesNotContain(catalog.Diagnostics, diagnostic => diagnostic.Level == AgentPluginLoadDiagnosticLevel.Error);

        var plugin = Assert.Single(catalog.Plugins);
        Assert.Equal("oscova.agents.plugins.echo", plugin.Manifest.Id);
        Assert.Equal("Oscova Echo Plugin", plugin.Manifest.Name);
        Assert.Equal("0.2.0", plugin.Manifest.Version);
        Assert.Equal("Oscova.Agents.Plugins.Echo.dll", plugin.Manifest.Assembly);
        Assert.Equal("Oscova.Agents.Plugins.Echo.EchoAgentPlugin", plugin.Manifest.EntryType);
        Assert.Equal(Path.GetFullPath(pluginDirectory), plugin.PluginDirectory);

        var contribution = Assert.Single(plugin.Toolsets);
        Assert.Equal("EchoToolset", contribution.ToolsetType.Name);
        Assert.True(typeof(ICrewToolset).IsAssignableFrom(contribution.ToolsetType));
        var context = new AgentPluginContext(
            new NullToolCredentialResolver(),
            plugin.PluginDirectory,
            modelStorageDirectory);
        var modelProvider = Assert.Single(plugin.ModelProviders);
        Assert.Equal("EchoModelProvider", modelProvider.ProviderType.Name);
        Assert.Equal("echo", modelProvider.Factory(context).Type);
        var synthesisProvider = Assert.Single(plugin.SpeechSynthesisProviders);
        Assert.Equal("EchoSpeechSynthesisProvider", synthesisProvider.ProviderType.Name);
        Assert.Equal("echo-tts", synthesisProvider.Factory(context).Type);
        Assert.Empty(plugin.SpeechRecognitionProviders);
        Assert.Contains("test-echo", plugin.DeclaredCredentialKeys);
        Assert.Contains("echo", plugin.DeclaredCredentialKeys);
        Assert.DoesNotContain("undeclared", plugin.DeclaredCredentialKeys);

        var toolset = Assert.IsAssignableFrom<ICrewToolset>(contribution.Factory(context));

        var tools = new CrewToolCollection();
        tools.Add(toolset, plugin.Manifest.Id);

        var qualifiedName = ToolNaming.BuildToolsetQualifiedName(plugin.Manifest.Id, "echo", "test_echo");
        var tool = tools.Find(qualifiedName);

        Assert.NotNull(tool);
        Assert.Equal("test_echo", tools.GetOriginalName(tool!));
    }

    [Fact]
    public void Constructor_RejectsRelativeRuntimeDirectories()
    {
        var absoluteDirectory = Path.Combine(Path.GetTempPath(), "oscova-plugin-context-tests");

        Assert.Throws<ArgumentException>(() => new AgentPluginContext(
            new NullToolCredentialResolver(),
            "relative-plugin",
            absoluteDirectory));
        Assert.Throws<ArgumentException>(() => new AgentPluginContext(
            new NullToolCredentialResolver(),
            absoluteDirectory,
            "relative-models"));
    }

    [Fact]
    public async Task EnsureDownloadAsync_ForwardsHostDownloadValues()
    {
        var root = Path.Combine(Path.GetTempPath(), "oscova-plugin-context-tests");
        using var cancellation = new CancellationTokenSource();
        var expectedToken = cancellation.Token;
        var destinationPath = Path.Combine(root, "models", "nested", "..", "model.bin");
        var expectedDestinationPath = Path.GetFullPath(destinationPath);
        (string Id, string Url, string DestinationPath, CancellationToken Token)? captured = null;
        var context = new AgentPluginContext(
            new NullToolCredentialResolver(),
            _ => new HttpClient(),
            NullLoggerFactory.Instance,
            () => TimeZoneInfo.Utc,
            Path.Combine(root, "plugin"),
            Path.Combine(root, "models"),
            (id, url, destinationPath, ct) =>
            {
                captured = (id, url, destinationPath, ct);
                return Task.CompletedTask;
            });

        await context.EnsureDownloadAsync("asset", "https://example.test/model", destinationPath, expectedToken);

        Assert.Equal(("asset", "https://example.test/model", expectedDestinationPath, expectedToken), captured);
    }

    [Fact]
    public async Task EnsureDownloadAsync_RejectsDestinationsOutsideModelStorage()
    {
        var root = Path.Combine(Path.GetTempPath(), "oscova-plugin-context-tests");
        var modelStorageDirectory = Path.Combine(root, "models");
        var delegateCalls = 0;
        var context = new AgentPluginContext(
            new NullToolCredentialResolver(),
            _ => new HttpClient(),
            NullLoggerFactory.Instance,
            () => TimeZoneInfo.Utc,
            Path.Combine(root, "plugin"),
            modelStorageDirectory,
            (_, _, _, _) =>
            {
                delegateCalls++;
                return Task.CompletedTask;
            });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            context.EnsureDownloadAsync("asset", "https://example.test/model", "model.bin"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            context.EnsureDownloadAsync("asset", "https://example.test/model", Path.Combine(root, "outside.bin")));
        Assert.Equal(0, delegateCalls);
    }

    [Fact]
    public async Task EnsureDownloadAsync_WithoutHostDownloadSupport_Throws()
    {
        var root = Path.Combine(Path.GetTempPath(), "oscova-plugin-context-tests");
        var context = new AgentPluginContext(
            new NullToolCredentialResolver(),
            Path.Combine(root, "plugin"),
            Path.Combine(root, "models"));

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            context.EnsureDownloadAsync("asset", "https://example.test/model", Path.Combine(root, "models", "model.bin")));
    }

    [Fact]
    public void InspectDirectory_ReturnsInstalledPluginWithoutLoadingIt()
    {
        using var pluginRoot = TestPluginRoot.Create();
        pluginRoot.AddTestPlugin();

        var inspection = Assert.Single(AgentPluginLoader.InspectDirectory(pluginRoot.RootPath));

        Assert.Null(inspection.Diagnostic);
        Assert.NotNull(inspection.Manifest);
        Assert.Equal("oscova.agents.plugins.echo", inspection.Manifest.Id);
    }

    [Fact]
    public void InspectDirectory_IgnoresInstallerStagingDirectories()
    {
        using var pluginRoot = TestPluginRoot.Create();
        pluginRoot.AddTestPlugin(".staging-plugin-leftover");

        var inspections = AgentPluginLoader.InspectDirectory(pluginRoot.RootPath);

        Assert.Empty(inspections);
    }

    [Fact]
    public void LoadDirectory_WithExplicitAllowlist_LoadsOnlyEnabledPlugin()
    {
        using var pluginRoot = TestPluginRoot.Create();
        pluginRoot.AddTestPlugin();

        var inactiveCatalog = AgentPluginLoader.LoadDirectory(
            pluginRoot.RootPath,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var enabledCatalog = AgentPluginLoader.LoadDirectory(
            pluginRoot.RootPath,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "oscova.agents.plugins.echo" });

        Assert.Empty(inactiveCatalog.Plugins);
        Assert.Single(enabledCatalog.Plugins);
        Assert.Equal("oscova.agents.plugins.echo", enabledCatalog.Plugins[0].Manifest.Id);
    }

    [Fact]
    public void LoadDirectory_RejectsDuplicatePluginIds()
    {
        using var pluginRoot = TestPluginRoot.Create();
        pluginRoot.AddTestPlugin("oscova.agents.plugins.echo-one");
        pluginRoot.AddTestPlugin("oscova.agents.plugins.echo-two");

        var catalog = AgentPluginLoader.LoadDirectory(pluginRoot.RootPath);

        Assert.Single(catalog.Plugins);
        Assert.Contains(catalog.Diagnostics, diagnostic =>
            diagnostic.Level == AgentPluginLoadDiagnosticLevel.Error &&
            diagnostic.Message.Contains("Duplicate plugin id 'oscova.agents.plugins.echo'", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadDirectory_ReportsMalformedManifestWithoutThrowing()
    {
        using var pluginRoot = TestPluginRoot.Create();
        pluginRoot.AddTestPlugin("broken", "{}");

        var catalog = AgentPluginLoader.LoadDirectory(pluginRoot.RootPath);

        Assert.Empty(catalog.Plugins);
        Assert.Contains(catalog.Diagnostics, diagnostic =>
            diagnostic.Level == AgentPluginLoadDiagnosticLevel.Error &&
            diagnostic.Message == "Plugin manifest must set Id.");
    }

    [Theory]
    [InlineData(" oscova.agents.plugins.echo", "Oscova.Agents.Plugins.Echo.dll", "Oscova.Agents.Plugins.Echo.EchoAgentPlugin", "Plugin manifest Id must not contain leading or trailing whitespace.")]
    [InlineData("oscova.agents.plugins.echo", "Oscova.Agents.Plugins.Echo.dll ", "Oscova.Agents.Plugins.Echo.EchoAgentPlugin", "Plugin manifest Assembly must not contain leading or trailing whitespace.")]
    [InlineData("oscova.agents.plugins.echo", "Oscova.Agents.Plugins.Echo.dll", " Oscova.Agents.Plugins.Echo.EchoAgentPlugin", "Plugin manifest EntryType must not contain leading or trailing whitespace.")]
    public void LoadDirectory_RejectsExecutionValuesWithSurroundingWhitespace(
        string pluginId,
        string assembly,
        string entryType,
        string expectedDiagnostic)
    {
        using var pluginRoot = TestPluginRoot.Create();
        pluginRoot.AddTestPlugin("whitespace", $$"""
            {
              "id": "{{pluginId}}",
              "name": "Oscova Echo Plugin",
              "version": "0.1.0",
              "assembly": "{{assembly}}",
              "entryType": "{{entryType}}"
            }
            """);

        var catalog = AgentPluginLoader.LoadDirectory(
            pluginRoot.RootPath,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "oscova.agents.plugins.echo" });

        Assert.Empty(catalog.Plugins);
        Assert.Contains(catalog.Diagnostics, diagnostic =>
            diagnostic.Level == AgentPluginLoadDiagnosticLevel.Error &&
            diagnostic.Message == expectedDiagnostic);
    }

    [Fact]
    public void LoadDirectory_ReportsMissingAssemblyWithoutThrowing()
    {
        using var pluginRoot = TestPluginRoot.Create();
        pluginRoot.AddTestPlugin("missing-assembly", """
            {
              "id": "missing.assembly",
              "name": "Missing Assembly",
              "version": "1.0.0",
              "assembly": "Missing.Plugin.dll",
              "entryType": "Missing.Plugin.Entry"
            }
            """);

        var catalog = AgentPluginLoader.LoadDirectory(pluginRoot.RootPath);

        Assert.Empty(catalog.Plugins);
        Assert.Contains(catalog.Diagnostics, diagnostic =>
            diagnostic.Level == AgentPluginLoadDiagnosticLevel.Error &&
            diagnostic.Message == "Plugin assembly 'Missing.Plugin.dll' was not found.");
    }

    [Fact]
    public void LoadDirectory_RejectsAssemblyPathOutsidePluginDirectory()
    {
        using var pluginRoot = TestPluginRoot.Create();
        pluginRoot.AddTestPlugin("escaped-assembly", """
            {
              "id": "escaped.assembly",
              "name": "Escaped Assembly",
              "version": "1.0.0",
              "assembly": "../Oscova.Agents.Plugins.Echo.dll",
              "entryType": "Oscova.Agents.Plugins.Echo.EchoAgentPlugin"
            }
            """);

        var catalog = AgentPluginLoader.LoadDirectory(pluginRoot.RootPath);

        Assert.Empty(catalog.Plugins);
        Assert.Contains(catalog.Diagnostics, diagnostic =>
            diagnostic.Level == AgentPluginLoadDiagnosticLevel.Error &&
            diagnostic.Message == "Plugin assembly path must stay within the plugin manifest directory.");
    }

    [Fact]
    public void LoadDirectory_RejectsRootedAssemblyPath()
    {
        using var pluginRoot = TestPluginRoot.Create();
        pluginRoot.AddTestPlugin("rooted-assembly", $$"""
            {
              "id": "rooted.assembly",
              "name": "Rooted Assembly",
              "version": "1.0.0",
              "assembly": {{JsonSerializer.Serialize(Path.Combine(Path.GetTempPath(), "Oscova.Agents.Plugins.Echo.dll"))}},
              "entryType": "Oscova.Agents.Plugins.Echo.EchoAgentPlugin"
            }
            """);

        var catalog = AgentPluginLoader.LoadDirectory(pluginRoot.RootPath);

        Assert.Empty(catalog.Plugins);
        Assert.Contains(catalog.Diagnostics, diagnostic =>
            diagnostic.Level == AgentPluginLoadDiagnosticLevel.Error &&
            diagnostic.Message == "Plugin assembly path must be relative to the plugin manifest directory.");
    }

    private sealed class NullToolCredentialResolver : IToolCredentialResolver
    {
        public Task<string?> ResolveAsync(string credentialKey, string fieldName, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<string?> ResolveAsync(string credentialKey, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }
}
