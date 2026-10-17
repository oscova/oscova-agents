# Plugin settings preview

This .NET 10 interactive server sample references the Docker component project and the public Oscova framework. It has no private Host assembly reference. The Idle, Busy, Unavailable, Success and Error choices exercise appearance only. Actions in this sample do not build an image, write settings or prove authorization.

Start a matching installed Oscova version, then point the sample at that origin:

```powershell
$env:OscovaResourcesBaseUrl = "http://localhost:11317/"
dotnet watch --project src/oscova-agents/samples/Oscova.Agents.PluginSettingsPreview --urls http://0.0.0.0:5180
```

For phone preview, set `OscovaResourcesBaseUrl` to the installed Oscova LAN origin so the phone can fetch its assets. Open the sample using the developer computer's LAN address and port 5180. Choose light or dark theme, then compare the panel at the same viewport size as Oscova.

The sample references the existing `app.css`, `custom-theme-bundle.css`, `custom-theme-overrides.css` and UI scripts named in `Components/App.razor`. For local resources, copy those files and their referenced font/script files unchanged from the matching installation into the preview's `wwwroot`, preserving relative paths. Point `OscovaResourcesBaseUrl` at the preview's own origin. This manual setup does not export, generate or distribute another theme.

Replace the component and typed context with your own project to preview another panel. Use installed-package tests for actual Host dialogs, storage, scopes and runtime effects. A CSS class absent from the installed theme remains unstyled.

For an independent page, use `PluginSettingsSlots.Page` with `PluginSettingsPage` metadata and a null association. The installed host supplies navigation around your component. Echo provides a working example with its own plaintext settings file.
