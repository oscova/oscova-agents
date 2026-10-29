# Plugin manifest

Every plugin installation needs an `oscova-plugin.json` file. Oscova reads this manifest to identify the plugin, find its assembly, and locate the `IAgentPlugin` entry type.

> **Important:**
> Passing manifest validation does not make a plugin trustworthy. Plugins run as trusted code inside the Oscova process, without a plugin sandbox.



## Create the manifest

Place the manifest in the plugin directory. The loader searches the configured plugin root recursively for the exact file name `oscova-plugin.json`.

```json
{
  "id": "example.hello",
  "name": "Example Hello Tools",
  "version": "1.0.0",
  "frameworkVersionRange": "[4.0.0,5.0.0)",
  "assembly": "Example.Hello.dll",
  "entryType": "Example.Hello.MyAgentPlugin"
}
```

| Field | Required | Contract |
|---|---:|---|
| `id` | Yes | Stable plugin identity without `:`. Duplicate IDs are compared case-insensitively. |
| `name` | Yes | Nonblank display name. |
| `version` | Yes | Nonblank plugin-owned version string. |
| `assembly` | Yes | Relative path from the manifest directory to the plugin assembly. |
| `entryType` | Yes | Fully qualified type name of the class that implements `IAgentPlugin`. |
| `frameworkVersionRange` | Yes | Supported framework releases, using the NuGet range syntax below. |
| `supportedRuntimeIdentifiers` | No | Supported release runtimes. Omitting the list permits `win-x64`, `linux-x64`, `osx-x64` and `osx-arm64`. |
| `dependencyAssets` | No | Extra files to retain from restored dependency packages during NuGet installation. Defaults to an empty array. |
| `hardwareRequirements` | No | A map from supported release RID to `backend` (`cpu`, `cuda`, `metal` or `managed`) and optional positive `cudaMajor`. Advisory metadata does not replace runtime admission. |

Do not add leading or trailing whitespace to `id`, `assembly`, or `entryType`. The assembly path must be relative, remain inside the manifest directory, and resolve to an existing file. A manually copied plugin may place the assembly in a subdirectory as long as those rules hold.

Declare platform restrictions in the manifest, for example `"supportedRuntimeIdentifiers": ["win-x64", "linux-x64", "osx-arm64"]` for a plugin without Intel Mac support. Oscova omits unsupported plugins from startup choices, rejects installation before restoring dependencies, and skips loading them with a diagnostic. The online catalog still lists them and explains the platform restriction when installation is attempted.

## Understand inspection and loading

Hardware declarations describe package requirements, independently of detection or successful execution. For example, `"hardwareRequirements": { "win-x64": { "backend": "cuda", "cudaMajor": 13 } }` declares a CUDA requirement. Missing, unknown or conflicting metadata leaves advice unknown and does not prevent an explicit install choice.

Official packaging derives tags from each edition's manifest: `oscova-hardware.<rid>.<backend>` plus `oscova-hardware.<rid>.cuda<major>` when a CUDA major is declared. For example, `oscova-hardware.win-x64.cuda` and `oscova-hardware.win-x64.cuda13` carry the declaration into the pre-install catalog without downloading native payloads. Keep tags and manifests consistent. Do not infer requirements from a package name or description.

`AgentPluginLoader.InspectDirectory(pluginRoot)` reads manifests and validates their required values and referenced assembly paths without loading plugin code. It returns an `AgentPluginInspection` for each discovered manifest, including an `AgentPluginLoadDiagnostic` when inspection fails.

Loading is a separate step:

- `LoadDirectory(pluginRoot)` attempts to load every valid discovered plugin.
- `LoadDirectory(pluginRoot, enabledPluginIds)` attempts to load only valid plugins selected by the supplied set.
- The loader finds `entryType` in the declared assembly, confirms that it implements `IAgentPlugin`, creates it with a public parameterless constructor, and calls `Configure`.
- Oscova admits the manifest's `frameworkVersionRange` against its bundled framework before loading enabled plugin code. A nonmatching or invalid range disables the plugin and reports the correction in Plugins settings.
- A missing type, incompatible type, constructor failure, or exception from `Configure` records an error diagnostic and skips that plugin.

The reusable loader inspects manifest structure and loads selected assemblies. Framework-range admission is performed by Oscova before that load. An embedding host using the loader directly must perform its own compatibility admission.

Discovery order is the case-insensitive manifest-path order. For duplicate IDs, the first valid manifest remains eligible to load and each later duplicate is skipped. A failure in one plugin does not stop other valid plugins.

An empty or missing plugin root returns an empty result. A root that cannot be searched returns a directory diagnostic instead of throwing from `InspectDirectory`.

## Package for online installation

### Prepare the framework compatibility declaration

Declare which `Oscova.Agents` framework versions your plugin supports. This range describes framework compatibility, independently of your plugin's release version.

Add `frameworkVersionRange` to `oscova-plugin.json`:

```json
{
  "id": "example.hello",
  "name": "Example Hello Tools",
  "version": "1.0.0",
  "frameworkVersionRange": "[4.0.0,5.0.0)",
  "assembly": "Example.Hello.dll",
  "entryType": "Example.Hello.MyAgentPlugin"
}
```

This is an illustrative plugin targeting major 4, not a declaration that every existing development release is compatible. The field is mandatory. A missing, blank, malformed, or nonmatching range makes that release ineligible for installation, update selection, and activation. There is no override for unknown compatibility.

An application upgrade automatically disables an installed plugin that cannot support the new framework. Its package, files, settings, and data remain installed. Oscova reports the reason in the update result and Plugins settings. A direct .NET tool update will apply the same check on the next Oscova startup. Install a compatible plugin release and explicitly enable it when you are ready to use it again.

Use NuGet's documented version-range syntax:

| Range | Meaning |
|---|---|
| `[4.0.0,5.0.0)` | At least version 4, excluding version 5 and later. This includes stable minor and patch releases in major 4. |
| `[4.0.0]` | Exactly version 4.0.0. |
| `[4.0.0,)` | Version 4 and every later version, including later majors. Use only if you deliberately support that broader range. |

The opening `[` includes the lower bound. The closing `)` excludes the upper bound. Choose the lower bound from the earliest framework API your plugin needs, and declare only versions you support. A plugin using a later-added API needs a higher minimum, even within the same major. During development, breaking changes can occur in minor releases, so use a range limited to the development releases you support.

Keep this declaration as the author-owned value. Packaging must publish the same range in the NuGet package manifest (`.nuspec`) as the standard `Oscova.Agents` dependency. For the example above, the fragment inside `<metadata>` is:

```xml
<dependencies>
  <group targetFramework="net10.0">
    <dependency id="Oscova.Agents" version="[4.0.0,5.0.0)" />
  </group>
</dependencies>
```

Retain your other dependencies in the applicable group. Generate the JSON and NuGet declarations from one value, and verify they agree in the built package. Keep your reproducible compilation reference separate from the supported range. A private compilation reference alone does not publish the dependency range.

NuGet exposes this dependency range through its registration metadata API, so Oscova can check candidate compatibility before downloading the plugin archive. The JSON declaration also lets a manually copied plugin describe compatibility without a registry request. The declaration is the publisher's support claim, and actual loading and execution can still fail.

### Current package structure

The online installer selects the newest compatible stable NuGet package release when all of these structural rules pass:

- The NuGet package type is `OscovaPlugin`.
- The package is no larger than 25 MB.
- `oscova-plugin.json` is at the package root.
- The declared assembly is a file name, not a path.
- The declared assembly is selected as a `net10.0` runtime asset.
- Ordinary managed, native, and resource dependencies are available for the current supported RID.
- The manifest ID is a folder-safe identifier contained beneath the plugin root.

A minimal project file follows the same shape as the shipped plugins:

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <EnableDynamicLoading>true</EnableDynamicLoading>
  <PackageType>OscovaPlugin</PackageType>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="Humanizer.Core" Version="3.0.10" />
  <None Include="oscova-plugin.json"
        Pack="true"
        PackagePath="\" />
</ItemGroup>
```

The installer restores the NuGet dependency graph for `net10.0` and the current supported RID, then installs the manifest, entry assembly, managed assemblies, native libraries, resource assemblies, and dependency files under `content/**` selected by NuGet. Dependency content remains scoped under that dependency's installed package directory. Other dependency files require an explicit `dependencyAssets` declaration. Root-package content is ignored, and package build targets are never executed. Dependencies on another `OscovaPlugin` package are not supported. Manual folder installation is less restrictive about layout, but the assembly and its dependencies must still be loadable by the running .NET 10 host.

Place root-package native assets for `win-x64`, `linux-x64`, `osx-x64`, and `osx-arm64` under conventional `runtimes/{rid}/native/...` paths. The installer retains the selected RID's relative tree, so deliberate variants can use the same native filename in different subdirectories such as `runtimes/{rid}/native/{variant}/{native-file}`. Native assets from ordinary dependency packages remain package-scoped. The package-size and structural limits above still apply.

### Retain additional dependency files

Some dependencies use build targets to copy files from a custom package directory. Declare the required files in `dependencyAssets` so installation can retain them without executing those targets. For example, a plugin depending on Microsoft.Playwright can retain its shared driver files and licence:

```json
{
  "dependencyAssets": [
    { "packageId": "Microsoft.Playwright", "path": ".playwright/package/" },
    { "packageId": "Microsoft.Playwright", "path": ".playwright/node/LICENSE" },
    { "packageId": "Microsoft.Playwright", "path": ".playwright/node/win32_x64/", "rid": "win-x64" },
    { "packageId": "Microsoft.Playwright", "path": ".playwright/node/linux-x64/", "rid": "linux-x64" },
    { "packageId": "Microsoft.Playwright", "path": ".playwright/node/darwin-x64/", "rid": "osx-x64" },
    { "packageId": "Microsoft.Playwright", "path": ".playwright/node/darwin-arm64/", "rid": "osx-arm64" }
  ]
}
```

Add this section to the complete manifest. The package must already be a dependency of the plugin. The declaration does not select a version or download another package.

| Entry field | Contract |
|---|---|
| `packageId` | Required dependency package ID, matched case-insensitively against the restored graph. The root plugin package is not allowed. |
| `path` | Required exact file path or directory ending in `/`. Use forward slashes and package-relative paths without `.` or `..` segments. Matching is case-sensitive. Wildcards are not supported. |
| `rid` | Optional host platform: `win-x64`, `linux-x64`, `osx-x64`, or `osx-arm64`. Omit it for shared files. |

Files stay under their dependency's installed package directory, preserving relative paths. A declaration for another supported platform is skipped. Missing packages, missing matching files, invalid paths and unsupported platform identifiers reject installation. Overlapping declarations copy the same source once. Existing containment and installed-size limits still apply. Retaining a script does not execute it, and executable permissions required by a runtime remain the plugin's responsibility. Manual folder installation does not process these declarations, so copy its complete runtime layout yourself.

## Update an installed plugin

An online install for an existing manifest ID stages a replacement for the next startup. It does not overwrite the loaded assembly. Oscova promotes the staged package before loading plugins on the next launch and retains the saved activation choice. A different NuGet package cannot claim an installed plugin identity silently: the administrator must confirm replacement of the exact installed package and manifest ID. A second install or update that competes for the same pending destination returns an ordinary conflict without a replacement action.

Keep the manifest ID stable across versions and publish a complete package. There is no hot reload. Uninstall requires an inactive plugin with its next-startup toggle off and removes any staged update, the installed package, and credentials and connected-account tokens owned by that plugin across users. Plugin-created data and settings remain.

## Know the trust boundary

Manifest and package checks verify structure, paths, required fields, and load compatibility. They do not verify:

- a code signature.
- the publisher's identity or reputation.
- the safety or intent of the assembly.

Administrators should review the package source, publisher, project, and license before installation, then explicitly enable the plugin for the next host restart.

## Troubleshooting

- **Plugin is not discovered:** confirm the file is named `oscova-plugin.json` and sits below the configured plugin root.
- **Assembly was not found:** make `assembly` relative to the manifest and copy the DLL to that resolved path.
- **Entry type was not found:** use the type's namespace-qualified name and confirm it is in the declared assembly.
- **Public parameterless constructor error:** add a public constructor with no parameters to the `IAgentPlugin` entry class.
- **Plugin failed to load:** inspect the diagnostic for an assembly-load, constructor, or `Configure` exception.
- **NuGet package is unsupported:** verify `OscovaPlugin`, the root manifest, and a declared entry assembly selected as a `net10.0` runtime asset.
- **Dependency restore fails:** publish every ordinary package dependency and supported-RID runtime asset to NuGet.org. Do not depend on another Oscova plugin.

## Related articles

- [IAgentPlugin](references/developer-guide-plugins-iagent-plugin.md)
- [Plugin Builder](references/developer-guide-plugins-plugin-builder.md)
- [Testing Plugins](references/developer-guide-examples-testing-plugins.md)
