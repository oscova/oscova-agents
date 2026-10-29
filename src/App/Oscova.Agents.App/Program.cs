using Microsoft.Extensions.DependencyInjection;
using Oscova.Agents.App.Cli;
using Oscova.Agents.App.Commands;
using Oscova.Agents.Infrastructure.Utils;
using Spectre.Console.Cli;
using Spectre.Console.Cli.Help;
using Spectre.Console.Cli.Unsafe;
using Oscova.Agents.Infrastructure.Hardware;

if (await LocalDiagnosticWorker.TryRunAsync(args).ConfigureAwait(false)) { return Environment.ExitCode; }

NativeRuntimeEnvironment.DisableOnnxTelemetry();

var services = new ServiceCollection();
services.AddLogging();
services.AddHttpClient();
services.AddOscovaCliServices(args);

var registrar = new TypeRegistrar(services);
var app = new CommandApp(registrar);
app.SetDefaultCommand<GatewayCommand>();

app.Configure(config =>
{
    config.SetApplicationName("oscova");
    config.SetApplicationVersion(ApplicationToolInstallation.GetAssemblyPackageVersion() ?? "dev");
    config.CaseSensitivity(CaseSensitivity.None);
    config.Settings.HelpProviderStyles = new HelpProviderStyle
    {
        Description = new DescriptionStyle { Header = CliTheme.Heading },
        Usage = new UsageStyle
        {
            Header = CliTheme.Heading,
            CurrentCommand = CliTheme.Heading,
            Command = CliTheme.Value,
            Options = CliTheme.Label,
            RequiredArgument = CliTheme.Value,
            OptionalArgument = CliTheme.Label
        },
        Examples = new ExampleStyle
        {
            Header = CliTheme.Heading,
            Arguments = CliTheme.Label
        },
        Arguments = new ArgumentStyle
        {
            Header = CliTheme.Heading,
            RequiredArgument = CliTheme.Value,
            OptionalArgument = CliTheme.Label
        },
        Commands = new CommandStyle
        {
            Header = CliTheme.Heading,
            ChildCommand = CliTheme.Value,
            RequiredArgument = CliTheme.Label
        },
        Options = new OptionStyle
        {
            Header = CliTheme.Heading,
            DefaultValueHeader = CliTheme.Label,
            DefaultValue = CliTheme.Value,
            RequiredOption = CliTheme.Value,
            RequiredOptionValue = CliTheme.Value,
            OptionalOptionValue = CliTheme.Label
        }
    };
    config.SetExceptionHandler((exception, _) => CliExceptionRenderer.Render(exception))
        .AddExample("start", "--detach")
        .AddExample("ask", "\"summarise my unread mail\"")
        .AddExample("ask", "\"continue\"", "--continue", "--stream")
        .AddExample("status", "--json")
        .AddExample("config", "set", "host", "http://localhost:5228");

    config.AddCommand<StartCommand>("start")
        .WithDescription("Launch the Oscova host, in the foreground by default or detached with --detach.")
        .WithExample("start")
        .WithExample("start", "--detach");
    config.AddCommand<NodeCommand>("node")
        .WithDescription("Connect this device's projects, or inspect and stop its enrolled node.")
        .WithExample("node", "--host", "http://server:5228", "--allow-http", "--detach")
        .WithExample("node", "status")
        .WithExample("node", "stop");

    config.AddCommand<StopCommand>("stop").WithDescription("Stop the local Oscova Host and Avatar MCP, or request a remote Host stop.");
    config.AddCommand<RestartCommand>("restart").WithDescription("Restart a running Oscova host.");
    config.AddCommand<StatusCommand>("status")
        .WithAlias("st")
        .WithDescription("Show host status.")
        .WithExample("status")
        .WithExample("status", "--json");
    config.AddCommand<VersionCommand>("version").WithDescription("Show the CLI version and, if reachable, the host version.");
    config.AddCommand<DoctorCommand>("doctor")
        .WithDescription("Run read-only diagnostics on the CLI, its host connection, and its credentials.")
        .WithExample("doctor")
        .WithExample("doctor", "--json");
    config.AddCommand<LoginCommand>("login").WithDescription("Sign in this CLI to an Oscova host.");
    config.AddCommand<LogoutCommand>("logout").WithDescription("Revoke and remove this CLI login.");
    config.AddCommand<WhoamiCommand>("whoami").WithDescription("Show CLI login and local control state.");
    config.AddCommand<AskCommand>("ask")
        .WithDescription("Ask an agent. Use --thread/--continue to keep context, --json or --jsonl for machine output (exit codes: 1 internal, 2 auth, 3 forbidden, 4 usage).")
        .WithExample("ask", "\"what is on my calendar today\"")
        .WithExample("ask", "\"and tomorrow\"", "--continue")
        .WithExample("ask", "\"summarise this\"", "--agent", "research", "--json");

    config.SafetyOff().AddBranch("avatar", typeof(CommandSettings), branch =>
    {
        branch.SetDescription("Install and control the optional Windows Avatar Engine.");
        branch.AddCommand("install", typeof(AvatarInstallCommand)).WithDescription("Download the Avatar Engine or install it from a local ZIP.");
        branch.AddCommand("start", typeof(AvatarStartCommand)).WithDescription("Start the installed Avatar Engine.");
        branch.AddCommand("status", typeof(AvatarStatusCommand)).WithDescription("Show Avatar Engine installation and process status.");
        branch.AddCommand("stop", typeof(AvatarStopCommand)).WithDescription("Stop the Avatar Engine process started by Oscova.");
        branch.AddCommand("uninstall", typeof(AvatarUninstallCommand)).WithDescription("Stop and remove the installed Avatar Engine.");
        branch.AddBranch("mcp", typeof(CommandSettings), mcp =>
        {
            mcp.SetDescription("Expose Avatar HTTP controls to local MCP clients independently of the Engine.");
            mcp.AddCommand("start", typeof(AvatarMcpStartCommand)).WithDescription("Start the detached localhost Avatar MCP service.");
            mcp.AddCommand("stop", typeof(AvatarMcpStopCommand)).WithDescription("Stop only the managed Avatar MCP service.");
            mcp.AddCommand("status", typeof(AvatarMcpStatusCommand)).WithDescription("Show MCP readiness and the saved Avatar target.");
            mcp.AddCommand("run", typeof(AvatarMcpRunCommand)).IsHidden();
        });
    });

    config.SafetyOff().AddBranch("agents", typeof(CommandSettings), branch =>
    {
        branch.SetDescription("Inspect the agents configured on the host.");
        branch.AddCommand("list", typeof(AgentsListCommand)).WithDescription("List every agent with its model and source.");
    });

    config.SafetyOff().AddBranch("models", typeof(CommandSettings), branch =>
    {
        branch.SetDescription("Inspect and test configured model providers.");
        branch.AddCommand("list", typeof(ModelsListCommand)).WithDescription("List the models available to agents.");
        branch.AddCommand("test", typeof(ModelsTestCommand)).WithDescription("Check that a configured provider answers.");
        branch.AddCommand("local", typeof(ModelsLocalCommand)).WithDescription("List the local model files installed on the host.");
        branch.AddCommand("available", typeof(ModelsAvailableCommand)).WithDescription("List the models one provider offers.");
    });

    config.SafetyOff().AddBranch("threads", typeof(CommandSettings), branch =>
    {
        branch.SetDescription("Manage chat threads.");
        branch.AddCommand("list", typeof(ThreadsListCommand)).WithDescription("List chat threads (newest first).");
        branch.AddCommand("show", typeof(ThreadsShowCommand)).WithDescription("Show one thread's details.");
        branch.AddCommand("messages", typeof(ThreadsMessagesCommand)).WithDescription("Print the messages in one thread.");
        branch.AddCommand("new", typeof(ThreadsNewCommand)).WithDescription("Start a new thread and print its id.");
        branch.AddCommand("rename", typeof(ThreadsRenameCommand)).WithDescription("Give a thread a new title.");
        branch.AddCommand("delete", typeof(ThreadsDeleteCommand)).WithDescription("Delete one thread.");
        branch.AddCommand("clear", typeof(ThreadsClearCommand)).WithDescription("Delete every thread.");
    });

    config.SafetyOff().AddBranch("tasks", typeof(CommandSettings), branch =>
    {
        branch.SetDescription("Manage and trigger scheduled agent tasks.");
        branch.AddCommand("list", typeof(TasksListCommand)).WithDescription("List scheduled tasks.");
        branch.AddCommand("show", typeof(TasksShowCommand)).WithDescription("Show one task's configuration.");
        branch.AddCommand("runs", typeof(TasksRunsCommand)).WithDescription("List a task's recent runs.");
        branch.AddCommand("run-show", typeof(TasksRunShowCommand)).WithDescription("Show one run's result.");
        branch.AddCommand("trigger", typeof(TasksTriggerCommand)).WithDescription("Run a task now.");
        branch.AddCommand("delete", typeof(TasksDeleteCommand)).WithDescription("Delete a task.");
    });

    config.SafetyOff().AddBranch("plugins", typeof(CommandSettings), branch =>
    {
        branch.SetDescription("Inspect and configure installed plugins.");
        branch.AddCommand("list", typeof(PluginsListCommand)).WithDescription("List installed plugins and their status.");
    });

    config.SafetyOff().AddBranch("skills", typeof(CommandSettings), branch =>
    {
        branch.SetDescription("Manage installed skills.");
        branch.AddCommand("list", typeof(SkillsListCommand)).WithDescription("List installed skills.");
        branch.AddCommand("import", typeof(SkillsImportCommand)).WithDescription("Import a skill package from a local file.");
        branch.AddCommand("remove", typeof(SkillsRemoveCommand)).WithDescription("Uninstall a skill.");
    });

    config.SafetyOff().AddBranch("mcp", typeof(CommandSettings), branch =>
    {
        branch.SetDescription("Manage MCP servers.");
        branch.AddCommand("list", typeof(McpListCommand)).WithDescription("List MCP servers and their connected tools.");
        branch.AddCommand("add", typeof(McpAddCommand)).WithDescription("Register an MCP server and connect to it.");
        branch.AddCommand("remove", typeof(McpRemoveCommand)).WithDescription("Remove an MCP server.");
        branch.AddCommand("restart", typeof(McpRestartCommand)).WithDescription("Reconnect one MCP server.");
        branch.AddCommand("reconnect", typeof(McpReconnectCommand)).WithDescription("Reconnect every MCP server.");
    });

    config.SafetyOff().AddBranch("logs", typeof(CommandSettings), branch =>
    {
        branch.SetDescription("Read and clear host logs.");
        branch.AddCommand("files", typeof(LogsFilesCommand)).WithDescription("List the log files on the host.");
        branch.AddCommand("read", typeof(LogsReadCommand)).WithDescription("Read one page of log entries.");
        branch.AddCommand("clear", typeof(LogsClearCommand)).WithDescription("Empty one log file.");
    });

    config.SafetyOff().AddBranch("notifications", typeof(CommandSettings), branch =>
    {
        branch.SetDescription("Read and clear the notification inbox.");
        branch.AddCommand("list", typeof(NotificationsListCommand)).WithDescription("List notifications, newest first.");
        branch.AddCommand("read", typeof(NotificationsReadCommand)).WithDescription("Mark one notification read.");
        branch.AddCommand("read-all", typeof(NotificationsReadAllCommand)).WithDescription("Mark every notification read.");
        branch.AddCommand("clear", typeof(NotificationsClearCommand)).WithDescription("Empty the notification inbox.");
    });

    config.SafetyOff().AddBranch("environment", typeof(CommandSettings), branch =>
    {
        branch.SetDescription("Export, restore, reset, and recover the Oscova environment.");
        branch.AddCommand("export", typeof(EnvironmentExportCommand)).WithDescription("Write a passphrase-sealed backup. Requires an administrator account.");
        branch.AddCommand("restore", typeof(EnvironmentRestoreCommand)).WithDescription("Restore a backup onto a fresh install and restart the host.");
        branch.AddCommand("reset", typeof(EnvironmentResetCommand)).WithDescription("Retain the current environment and restart with fresh app data.");
        branch.AddCommand("recover", typeof(EnvironmentRecoverCommand)).WithDescription("Recover the previous environment after an interrupted restore.");
        branch.AddCommand("erase", typeof(EnvironmentEraseCommand)).WithDescription("Permanently erase all owned local Oscova data, including Avatar and credentials.");
    });

    config.AddBranch<CommandSettings>("update", branch =>
    {
        branch.SetDescription("Update Oscova and its plugins to the latest stable version, or to --version.");
        branch.SetDefaultCommand<ApplicationUpdateCommand>();
        // Help lists only the branch options, so the examples show the default command's --version.
        branch.AddExample("update");
        branch.AddExample("update", "--version", "<VERSION>");
        branch.AddCommand<ApplicationUpdateStatusCommand>("status").WithDescription("Show the latest application update attempt.");
    });

    config.SafetyOff().AddBranch("tools", typeof(CommandSettings), branch =>
    {
        branch.SetDescription("Inspect tool governance policy and quota usage.");
        branch.AddCommand("list", typeof(ToolsListCommand)).WithDescription("List governed tools with their network scope and call budget.");
    });

    config.SafetyOff().AddBranch("egress", typeof(CommandSettings), branch =>
    {
        branch.SetDescription("Inspect the outbound network audit ledger.");
        branch.AddCommand("list", typeof(EgressListCommand)).WithDescription("Read one page of the egress ledger.");
    });

    config.SafetyOff().AddBranch("config", typeof(CommandSettings), branch =>
    {
        branch.SetDescription("Read and write local CLI settings stored in ~/.oscova/cli-config.yaml.");
        branch.AddCommand("get", typeof(ConfigGetCommand)).WithDescription("Print one CLI setting, or all of them.");
        branch.AddCommand("set", typeof(ConfigSetCommand)).WithDescription("Change one CLI setting.");
    });
});

return await app.RunAsync(args).ConfigureAwait(false);
