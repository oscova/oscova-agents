# Testing plugins

Test the narrowest public boundary that proves your plugin works. Start with `AgentPluginLoader` for manifest and contribution behavior, then test actual tool and provider operations. The public `Oscova.Agents` repository contains framework tests in `tests/Oscova.Agents.Tests` and first-party plugin tests in `tests/Oscova.Agents.Plugins.Tests`. Application activation and real agent runs require a separate host integration check.

> **Note:**
> A loader test proves structure and registration. It does not prove that a plugin is safe to trust, enabled in the host, allowed for an agent, or able to complete an external network call.

## Prepare a compiled fixture

Loader tests need a real compiled plugin assembly. The repository's framework test project keeps the Echo build in the project graph without referencing its types:

```xml
<ProjectReference Include="..\..\src\Oscova.Agents\Oscova.Agents.csproj" />
<ProjectReference Include="..\Oscova.Agents.Plugins.Echo\Oscova.Agents.Plugins.Echo.csproj"
                  ReferenceOutputAssembly="false"
                  Private="false" />
```

Adapt those paths for your solution. Before each test:

1. Create a unique temporary plugin root.
2. Create one child directory for the plugin.
3. Copy the plugin's `bin/<Configuration>/net10.0/` output into that child directory.
4. Copy its manifest as `oscova-plugin.json`.
5. Delete the temporary root during test cleanup.

Copy the complete output rather than one DLL so plugin-private dependencies and dependency metadata stay available. Do not load directly from a developer installation under `app_data/plugins`, because shared state makes tests order-dependent.

## Test manifest inspection and loading

Call the public `AgentPluginLoader.InspectDirectory` or `LoadDirectory` methods. Assert both successful plugins and `AgentPluginLoadDiagnostic` values.

This minimal xUnit test needs no compiled assembly because the manifest fails required-field validation first:

```csharp
using Oscova.Agents.Plugins;

public sealed class PluginManifestTests
{
    [Fact]
    public void LoadDirectory_MissingId_ReturnsDiagnostic()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "example-plugin-tests",
            Guid.NewGuid().ToString("N"));
        var pluginDirectory = Path.Combine(root, "broken");
        Directory.CreateDirectory(pluginDirectory);

        try
        {
            File.WriteAllText(
                Path.Combine(pluginDirectory, AgentPluginLoader.ManifestFileName),
                "{}");

            var catalog = AgentPluginLoader.LoadDirectory(root);

            Assert.Empty(catalog.Plugins);
            var diagnostic = Assert.Single(catalog.Diagnostics);
            Assert.Equal(AgentPluginLoadDiagnosticLevel.Error, diagnostic.Level);
            Assert.Equal("Plugin manifest must set Id.", diagnostic.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
```

This proves that an incomplete, well-formed JSON manifest returns a loader diagnostic instead of a loaded plugin. It does not prove invalid JSON parsing, assembly loading, or entry-type validation.

For a valid compiled fixture, assert:

- the exact `Id`, `Name`, `Version`, `Assembly`, and `EntryType` values.
- the expected toolset and provider contribution types.
- the absence of error diagnostics.
- the behavior of the overload that accepts `IReadOnlySet<string> enabledPluginIds`.

The production loader also rejects missing or incompatible entry types and entry types that cannot be created with a public parameterless constructor. Add focused fixture assemblies for those cases when you change that boundary.

## Test reflected tools

After loading the plugin, invoke its public toolset factory with an `AgentPluginContext`, add the result to `AgentToolCollection` using the manifest ID as the origin, and look up the expected qualified name:

```csharp
using Oscova.Agents;
using Oscova.Agents.Collections;
using Oscova.Agents.Plugins;
using Oscova.Agents.Toolsets;
using Microsoft.Extensions.Logging.Abstractions;

var plugin = Assert.Single(catalog.Plugins);
var contribution = Assert.Single(
    plugin.Toolsets, item => item.ToolsetType.Name == "EchoToolset");
var modelStorageDirectory = Path.Combine(
    Path.GetTempPath(),
    "oscova-plugin-tests",
    "models");
var context = new AgentPluginContext(
    new NullCredentialResolver(),
    new TestPluginServices(),
    NullLoggerFactory.Instance,
    TimeProvider.System,
    plugin.PluginDirectory,
    modelStorageDirectory);
var toolset = Assert.IsAssignableFrom<IAgentToolset>(
    contribution.Factory(context));

var tools = new AgentToolCollection();
tools.Add(toolset, plugin.Manifest.Id);

var qualifiedName = ToolNaming.BuildToolsetQualifiedName(
    plugin.Manifest.Id,
    "echo",
    "test_echo");
var tool = tools.Find(qualifiedName);

Assert.NotNull(tool);
Assert.Equal("test_echo", tools.GetOriginalName(tool!));
```

Add these nested helpers to the test class. The resolver returns no secret, and unused Host operations fail explicitly:

```csharp
private sealed class NullCredentialResolver : IToolCredentialResolver
{
    public Task<string?> ResolveAsync(
        string credentialKey,
        string fieldName,
        CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    public Task<string?> ResolveAsync(
        string credentialKey,
        CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);
}

private sealed class TestPluginServices : IAgentPluginServices
{
    public IPluginEncryptionService Encryption => throw new NotSupportedException();

    public IToolOutputStore? ToolOutputStore => null;

    public HttpClient CreateHttpClient() => throw new NotSupportedException();

    public HttpMessageHandler CreateHttpMessageHandler() => throw new NotSupportedException();

    public Task EnsureDownloadAsync(PluginDownloadRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<string?> ResolveAccessTokenAsync(string providerId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
```

This reflection case does not send HTTP requests. For egress behavior, use the actual Host guard as the transport and assert the request outcome. The Host supplies `IAgentPluginServices` in production. Only `[AgentTool]` methods on the registered toolset appear in the reflected collection.

This test proves that the registered factory creates the declared toolset and that normal reflection publishes the Echo method as `oscova_agents_plugins_echo__echo__test_echo`. It does not invoke the tool or prove host activation.

Test `[ToolNetworkScope]` and `[ToolPolicy]` on a toolset that actually declares those attributes. The Echo tool declares neither, so it cannot prove non-default governance metadata.

## Match the current focused evidence

The repository's focused tests currently prove these boundaries:

| Test | What it proves |
| --- | --- |
| `LoadDirectory_ReturnsEmptyCatalogForMissingRoot` | A missing directory produces an empty catalog and no diagnostics. |
| `LoadDirectory_LoadsTestPluginAndReflectsToolset` | The Echo manifest, toolset, model provider, speech provider, and reflected name load through the public loader. |
| `InspectDirectory_ReturnsInstalledPluginWithoutLoadingIt` | Manifest inspection returns the installed Echo manifest without calling `LoadDirectory`. |
| `InspectDirectory_IgnoresInstallerStagingDirectories` | Pending installer directories are excluded from discovery. |
| `LoadDirectory_WithExplicitAllowlist_LoadsOnlyEnabledPlugin` | The allowlist overload skips an unselected plugin and loads a selected one. |
| `LoadDirectory_LoadsDefaultWebPluginToolsets` in the plugin test project | The live Web plugin creates `WebSearchToolset` and `NewsToolset` from a host-supplied context. |
| `LoadDirectory_RejectsDuplicatePluginIds` | Only one case-insensitive duplicate ID loads and an error diagnostic is returned. |
| `LoadDirectory_ReportsMalformedManifestWithoutThrowing` | The checked `{}` manifest is rejected specifically because `id` is missing. |
| `LoadDirectory_RejectsInvalidExecutionValues` | Invalid execution identity values, including leading or trailing whitespace in `id`, `assembly`, or `entryType`, are rejected. |
| `LoadDirectory_ReportsMissingAssemblyWithoutThrowing` | A missing declared assembly returns an error diagnostic. |
| `LoadDirectory_RejectsAssemblyPathOutsidePluginDirectory` | A relative path escape is rejected. |
| `LoadDirectory_RejectsRootedAssemblyPath` | An absolute assembly path is rejected. |
| `Constructor_RejectsRelativeRuntimeDirectories` | Context construction rejects relative plugin and model directories. |
| `EnsureDownloadAsync_RejectsDestinationsOutsideModelStorage` | Tracked downloads reject a destination outside the supplied model root. |

## Check host behavior in a running host

Authentication, persisted activation settings, startup, merged provider catalogs and model-backed runs depend on the host. Check them by installing and enabling your plugin in a running Oscova instance.

Keep loader failures in framework tests. A full host boot adds no useful evidence for a malformed manifest or path-containment assertion.

For model-provider panels, use browser journeys that select options, click Save or Cancel, reopen the editor and verify the saved provider. Include independent copies, type changes and shared-owner denial. Rendering a label alone does not prove a Razor event handler is bound.

Keep real native loads opt-in with local fixtures and bounded worker supervision. Missing optional test assets can skip before loading, while inference failure on an opted-in supported target must fail. Exercise crash, timeout, cancellation and parent exit with controlled child fixtures. A local Test pass proves a decode, and acceleration requires observed backend/offload evidence. Test CPU/CUDA edition replacement and restart using installed packages, separately from manifest inspection. Use identical model hash, context, prompt and token limit for performance comparisons.

## Treat NuGet installation as a separate boundary

The online installer adds package-specific checks that `AgentPluginLoader` does not cover:

- package type `OscovaPlugin`.
- `oscova-plugin.json` at the package root.
- the declared entry DLL selected as a `net10.0` runtime asset, plus its restored runtime dependencies.
- a total package size no greater than 25 MB.
- a folder-safe manifest ID, with existing installations updated through next-startup staging.

For your package, verify the root manifest, selected runtime assets, ordinary dependency graph, size limit, and offline loading from the installed output. Keep a real invocation in the success path, such as the Echo fixture's `Pluralize("person")` returning `people`. Catalog assertions alone do not prove dependency loading or operation success.

## Run focused commands

From the public `Oscova.Agents` repository, run the loader framework tests:

```powershell
dotnet test tests/Oscova.Agents.Tests/Oscova.Agents.Tests.csproj --filter "FullyQualifiedName~Framework.Plugins"
```

Run reflected-tool metadata tests:

```powershell
dotnet test tests/Oscova.Agents.Tests/Oscova.Agents.Tests.csproj --filter "FullyQualifiedName~Framework.Collections.CrewToolCollectionTests"
```

Run the Web-plugin contribution and loading tests:

```powershell
dotnet test tests/Oscova.Agents.Plugins.Tests/Oscova.Agents.Plugins.Tests.csproj --filter "FullyQualifiedName~WebAgentPluginTests|FullyQualifiedName~WebPluginLoadingTests"
```

## Troubleshooting

**The fixture output cannot be found**

Keep the plugin project in the test build graph and derive `Debug` or `Release` consistently. Do not silently fall back to stale output from another configuration.

**The valid plugin test reports a missing assembly**

Copy the compiled output into the same temporary child directory as `oscova-plugin.json`. The manifest's `assembly` value is resolved relative to that file.

**The plugin loads but the expected tool is missing**

Instantiate the contribution factory, pass the manifest ID to `AgentToolCollection.Add`, and calculate the name with `ToolNaming.BuildToolsetQualifiedName`.

**A host test passes without proving startup**

Install and enable the fixture before the throwaway host starts. Changing the activation allowlist after startup proves only persistence for the next restart.

**A registry test is skipped**

Check its prerequisite before treating the result as package-install evidence. A skipped NuGet.org or Ollama test does not prove that external boundary.

## Related articles

- [Echo Plugin](references/developer-guide-examples-echo-plugin.md)
- [Plugin Manifest](references/developer-guide-plugins-manifest.md)
- [Toolsets](references/developer-guide-toolsets-toolsets.md)

## Compiled settings example

Echo includes an independent compiled settings page in Preferences. Click its actual Save note button to verify the plugin-owned plaintext file in the current user's settings directory. The packaged component, satellite resources and shared Blazor type identities are part of plugin-loader acceptance. See [Compiled Plugin Settings](references/developer-guide-plugins-compiled-settings.md). Preview appearances alone do not prove admission, authorization or execution.
