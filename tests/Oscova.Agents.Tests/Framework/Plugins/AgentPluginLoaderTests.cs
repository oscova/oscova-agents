using Oscova.Agents.Collections;
using Oscova.Agents.Plugins;
using Oscova.Agents.Tests.TestSupport.TestPlugin;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Oscova.Agents.Tasks;
using Oscova.Agents.Credentials;

namespace Oscova.Agents.Tests.Framework.Plugins;

public sealed class AgentPluginLoaderTests
{
    /// <summary>Invalid instruction declarations become diagnostics without replacing accepted defaults.</summary>
    [Fact]
    public void AddInstruction_RejectsDuplicateAndUnsafeKeys_AndRetainsDefaultData()
    {
        var builder = new AgentPluginBuilder()
            .AddInstruction("browser-use", "Browser Instruction", "Default Markdown")
            .AddInstruction("browser-use", "Duplicate", "Other Markdown")
            .AddInstruction("../unsafe", "Unsafe", "Other Markdown");
        var contribution = Assert.Single(builder.Instructions);
        Assert.Equal("browser-use", contribution.Key);
        Assert.Equal("Browser Instruction", contribution.Label);
        Assert.Equal("Default Markdown", contribution.DefaultInstruction);
        Assert.Equal(2, builder.Diagnostics.Count);
    }

    /// <summary>Admits independent page registrations without an inline association or a component factory call.</summary>
    [Fact]
    public void IndependentPage_RegistersWithoutAssociationOrFactoryExecution()
    {
        var builder = new AgentPluginBuilder().AddSettingsPanel(new("settings", PluginSettingsSlots.Page, null,
            typeof(object), _ => throw new InvalidOperationException("Context creation is interactive only."), PluginSettingsAccess.CurrentUser)
        { Page = new("workspace", "Example") });
        Assert.Empty(builder.Diagnostics);
        Assert.Null(Assert.Single(builder.SettingsPanels).AssociationId);
        builder.AddSettingsPanel(new("inline", PluginSettingsSlots.ExecutionEnvironment, null,
            typeof(object), context => context, PluginSettingsAccess.CurrentUser));
        Assert.Single(builder.Diagnostics);
        Assert.Single(builder.SettingsPanels);
    }

    [Fact]
    public void TaskOverloads_RegisterWithoutCreatingHandlers()
    {
        var calls = 0;
        var builder = new AgentPluginBuilder()
            .AddTask("defaults", _ => new AgentTaskDefinition { Name = "Defaults" })
            .AddTask("deterministic", _ => new AgentTaskDefinition { Name = "Deterministic" },
                deterministicHandlerFactory: _ => { calls++; throw new InvalidOperationException("Factory ran during registration."); })
            .AddTask("agent", _ => new AgentTaskDefinition { Name = "Agent" },
                agentHandlerFactory: _ => { calls++; throw new InvalidOperationException("Factory ran during registration."); });

        Assert.Equal(["defaults", "deterministic", "agent"], builder.Tasks.Select(task => task.Key));
        Assert.Null(builder.Tasks[0].DeterministicHandlerFactory);
        Assert.Null(builder.Tasks[0].AgentHandlerFactory);
        Assert.NotNull(builder.Tasks[1].DeterministicHandlerFactory);
        Assert.NotNull(builder.Tasks[2].AgentHandlerFactory);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void InvalidCredential_DoesNotDiscardSurroundingContributions()
    {
        var builder = new AgentPluginBuilder()
            .AddTask("before", _ => new AgentTaskDefinition { Name = "Before" })
            .AddCredential(new CredentialDeclaration { Key = "invalid", Label = "Invalid" })
            .AddTask("after", _ => new AgentTaskDefinition { Name = "After" });

        Assert.Equal(["before", "after"], builder.Tasks.Select(task => task.Key));
        Assert.Empty(builder.Credentials);
        Assert.Contains(builder.Diagnostics, message => message.Contains("Credential 'invalid'", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadDirectory_RetainsTaskContributions()
    {
        using var pluginRoot = TestPluginRoot.Create();
        pluginRoot.AddTestPlugin();

        var plugin = Assert.Single(AgentPluginLoader.LoadDirectory(pluginRoot.RootPath).Plugins);
        var task = Assert.Single(plugin.Tasks);
        var context = CreateContext(plugin.PluginDirectory, Path.Combine(pluginRoot.RootPath, "models"));

        Assert.Equal("record-run", task.Key);
        Assert.Equal("Record plugin task run", task.DefinitionFactory(context).Name);
        Assert.NotNull(task.DeterministicHandlerFactory!(context));
        Assert.Null(task.AgentHandlerFactory);
    }

    [Fact]
    public void AddTask_RejectsInvalidDeclarations()
    {
        var builder = new AgentPluginBuilder();
        builder.AddTask("daily-check", _ => new AgentTaskDefinition())
            .AddTask("daily-check", _ => new AgentTaskDefinition())
            .AddTask("../unsafe", _ => new AgentTaskDefinition())
            .AddTask("Uppercase", _ => new AgentTaskDefinition());
        Assert.Single(builder.Tasks);
        Assert.Equal(3, builder.Diagnostics.Count);
    }

    [Fact]
    public void AddAgentTemplate_ValidatesMetadataAndDuplicateLocalIds()
    {
        var builder = new AgentPluginBuilder();
        var template = new AgentTemplateDefinition
        {
            Id = "release-notes", Name = "Release Notes", Description = "Drafts notes",
            SystemInstruction = "Write release notes."
        };
        Assert.Same(builder, builder.AddAgentTemplate(template));
        Assert.Same(template, Assert.Single(builder.AgentTemplates));
        builder.AddAgentTemplate(template with { Id = "Invalid" })
            .AddAgentTemplate(template with { Name = " " })
            .AddAgentTemplate(template);
        Assert.Single(builder.AgentTemplates);
        Assert.Equal(3, builder.Diagnostics.Count);
    }

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
        Assert.Equal(plugin.Assembly.GetName().Version!.ToString(3), plugin.Manifest.Version);
        Assert.Equal("Oscova.Agents.Plugins.Echo.dll", plugin.Manifest.Assembly);
        Assert.Equal("Oscova.Agents.Plugins.Echo.EchoAgentPlugin", plugin.Manifest.EntryType);
        Assert.Equal(Path.GetFullPath(pluginDirectory), plugin.PluginDirectory);

        Assert.Equal(2, plugin.Toolsets.Count);
        Assert.True(Assert.Single(plugin.Toolsets, item => item.ToolsetType.Name == "EchoProjectToolset").SupportsNodeExecution);
        var contribution = Assert.Single(plugin.Toolsets, item => item.ToolsetType.Name == "EchoToolset");
        Assert.Equal("EchoToolset", contribution.ToolsetType.Name);
        Assert.True(typeof(IAgentToolset).IsAssignableFrom(contribution.ToolsetType));
        var context = CreateContext(plugin.PluginDirectory, modelStorageDirectory);
        var modelProvider = Assert.Single(plugin.ModelProviders);
        Assert.Equal("EchoModelProvider", modelProvider.ProviderType.Name);
        Assert.Equal("echo", modelProvider.Factory(context).Type);
        var synthesisProvider = Assert.Single(plugin.SpeechSynthesisProviders);
        Assert.Equal("EchoSpeechSynthesisProvider", synthesisProvider.ProviderType.Name);
        Assert.Equal("echo-tts", synthesisProvider.Factory(context).Type);
        Assert.Empty(plugin.SpeechRecognitionProviders);
        Assert.Contains("test-echo", plugin.DeclaredCredentialKeys);
        Assert.Contains("echo", plugin.DeclaredCredentialKeys);
        Assert.Equal("echo-channel", Assert.Single(plugin.Credentials).Key);
        Assert.Contains("echo-channel", plugin.DeclaredCredentialKeys);
        Assert.Equal("echo", Assert.Single(plugin.Channels).Definition.Id);
        Assert.DoesNotContain("undeclared", plugin.DeclaredCredentialKeys);

        var toolset = Assert.IsAssignableFrom<IAgentToolset>(contribution.Factory(context));

        var tools = new AgentToolCollection();
        tools.Add(toolset, plugin.Manifest.Id);

        var qualifiedName = ToolNaming.BuildToolsetQualifiedName(plugin.Manifest.Id, "echo", "test_echo");
        var tool = tools.Find(qualifiedName);

        Assert.NotNull(tool);
        Assert.Equal("test_echo", tools.GetOriginalName(tool!));
    }

    /// <summary>Proves an invoked plugin dependency releases its DLL for Windows cleanup.</summary>
    [SkippableFact]
    public async Task LoadDirectory_InvokedManagedDependency_ReleasesItsFile()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows file locking is required for this regression.");

        using var pluginRoot = TestPluginRoot.Create();
        var pluginDirectory = pluginRoot.AddTestPlugin();
        var catalog = AgentPluginLoader.LoadDirectory(pluginRoot.RootPath);
        var plugin = Assert.Single(catalog.Plugins);
        var context = CreateContext(pluginDirectory, Path.Combine(pluginRoot.RootPath, "models"));
        var tools = new AgentToolCollection();
        tools.Add(Assert.Single(plugin.Toolsets, item => item.ToolsetType.Name == "EchoToolset").Factory(context), plugin.Manifest.Id);
        var qualifiedName = ToolNaming.BuildToolsetQualifiedName(plugin.Manifest.Id, "echo", "test_pluralize");
        var pluralize = tools.Find(qualifiedName);
        Assert.NotNull(pluralize);

        var result = await pluralize.InvokeAsync(new AIFunctionArguments { ["text"] = "cat" });

        Assert.Equal("cats", Assert.IsType<JsonElement>(result).GetString());
        var dependencyPath = Path.Combine(pluginDirectory, "Humanizer.dll");
        Assert.True(File.Exists(dependencyPath));
        File.Delete(dependencyPath);
        Assert.False(File.Exists(dependencyPath));
    }

    [Fact]
    public void Constructor_RejectsRelativeRuntimeDirectories()
    {
        var absoluteDirectory = Path.Combine(Path.GetTempPath(), "oscova-plugin-context-tests");

        Assert.Throws<ArgumentException>(() => CreateContext("relative-plugin", absoluteDirectory));
        Assert.Throws<ArgumentException>(() => CreateContext(absoluteDirectory, "relative-models"));
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
        var services = new TestPluginServices((request, ct) =>
        {
            captured = (request.Id, request.Url, request.DestinationPath, ct);
            return Task.CompletedTask;
        });
        var context = CreateContext(Path.Combine(root, "plugin"), Path.Combine(root, "models"), services);

        await context.EnsureDownloadAsync("asset", "https://example.test/model", destinationPath, expectedToken);

        Assert.Equal(("asset", "https://example.test/model", expectedDestinationPath, expectedToken), captured);
    }

    [Fact]
    public async Task EnsureDownloadAsync_RejectsDestinationsOutsideModelStorage()
    {
        var root = Path.Combine(Path.GetTempPath(), "oscova-plugin-context-tests");
        var modelStorageDirectory = Path.Combine(root, "models");
        var delegateCalls = 0;
        var services = new TestPluginServices((_, _) =>
        {
            delegateCalls++;
            return Task.CompletedTask;
        });
        var context = CreateContext(Path.Combine(root, "plugin"), modelStorageDirectory, services);

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
        var context = CreateContext(Path.Combine(root, "plugin"), Path.Combine(root, "models"));

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
    public void InspectDirectory_IgnoresInstallerDotDirectories()
    {
        using var pluginRoot = TestPluginRoot.Create();
        pluginRoot.AddTestPlugin(".staging-plugin-leftover");
        pluginRoot.AddTestPlugin(".pending-updates");

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
    [InlineData("vendor:extension", "Oscova.Agents.Plugins.Echo.dll", "Oscova.Agents.Plugins.Echo.EchoAgentPlugin", "Plugin manifest Id must not contain ':' because it separates credential namespaces.")]
    [InlineData("oscova.agents.plugins.echo", "Oscova.Agents.Plugins.Echo.dll ", "Oscova.Agents.Plugins.Echo.EchoAgentPlugin", "Plugin manifest Assembly must not contain leading or trailing whitespace.")]
    [InlineData("oscova.agents.plugins.echo", "Oscova.Agents.Plugins.Echo.dll", " Oscova.Agents.Plugins.Echo.EchoAgentPlugin", "Plugin manifest EntryType must not contain leading or trailing whitespace.")]
    public void LoadDirectory_RejectsInvalidExecutionValues(
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

    private static AgentPluginContext CreateContext(string pluginDirectory, string modelDirectory,
        IAgentPluginServices? services = null)
        => new(new NullToolCredentialResolver(), services ?? new TestPluginServices(),
            NullLoggerFactory.Instance, TimeProvider.System, pluginDirectory, modelDirectory)
        {
            UserDataDirectory = Path.Combine(modelDirectory, "user")
        };

    private sealed class TestPluginServices(
        Func<PluginDownloadRequest, CancellationToken, Task>? download = null) : IAgentPluginServices, IPluginEncryptionService
    {
        public Oscova.Agents.Toolsets.IToolOutputStore? ToolOutputStore => null;
        public IPluginEncryptionService Encryption => this;
        /// <inheritdoc />
        public string Encrypt(string plaintext)
            => throw new NotSupportedException("Plugin encryption is not configured in this fixture.");
        /// <inheritdoc />
        public string Decrypt(string ciphertext)
            => throw new NotSupportedException("Plugin encryption is not configured in this fixture.");
        public HttpClient CreateHttpClient() => new();
        public HttpMessageHandler CreateHttpMessageHandler() => new HttpClientHandler();
        public Task EnsureDownloadAsync(PluginDownloadRequest request, CancellationToken ct = default)
            => download?.Invoke(request, ct) ?? throw new NotSupportedException();
        public Task<string?> ResolveAccessTokenAsync(string providerId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }
}
