# Oscova Microsoft MXC Execution

An Oscova Agents plugin that provides native operating-system containment for shell commands and bundled skill scripts through Microsoft.Mxc.Sdk. Existing workspace grants and sandbox network permission apply.

Oscova installs this plugin by default. The operator selects Microsoft MXC in Settings > Agents & AI > Execution and saves. Windows uses ProcessContainer, Linux requires Bubblewrap, and macOS uses Seatbelt on Apple Silicon.

The package contributes its execution provider and a compiled Razor settings component through Oscova's plugin contracts. It uses the host theme and supplies no stylesheet.

See [Execution](https://oscova.ai/docs/articles/user-guide/settings-and-admin/execution.html) for prerequisites and limits.

[Oscova](https://oscova.ai) · [Documentation](https://oscova.ai/docs/) · [Source](https://github.com/oscova/oscova-agents)
