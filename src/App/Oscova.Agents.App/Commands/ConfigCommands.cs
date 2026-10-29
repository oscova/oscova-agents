using System.ComponentModel;
using Oscova.Agents.App.Cli;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Oscova.Agents.App.Commands;

public sealed class ConfigGetCommand(CliConfigStore configStore) : AsyncCommand<ConfigGetCommand.Settings>
{
    public sealed class Settings : OscovaCommandSettings
    {
        [CommandArgument(0, "[KEY]")]
        [Description("Setting to read: host, default-agent-id, or default-stream. Omit it to print every setting.")]
        public string? Key { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        CliOutput.Configure(settings);
        var config = configStore.Read();
        object? value = settings.Key?.ToLowerInvariant() switch
        {
            null => config,
            "host" => config.Host,
            "defaultagentid" or "default-agent-id" => config.DefaultAgentId,
            "defaultstream" or "default-stream" => config.DefaultStream,
            _ => throw new CliExitException(4, $"Unknown config key '{settings.Key}'.")
        };

        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(value, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var rich = !settings.Quiet &&
            AnsiConsole.Profile.Capabilities.Interactive && AnsiConsole.Profile.Capabilities.Ansi;
        // The full configuration needs rows because its object has no display text.
        if (!rich && settings.Key is not null)
        {
            AnsiConsole.WriteLine(value?.ToString() ?? string.Empty);
            return 0;
        }

        if (settings.Key is not null)
        {
            CliRender.KeyValue("Configuration",
            [
                ("Setting", CliRender.Identifier(settings.Key)),
                ("Value", CliRender.Value(value?.ToString() ?? string.Empty))
            ]);
            return 0;
        }

        var table = CliRender.Table("Setting", "Value");
        table.AddRow(CliRender.Identifier("host"), CliRender.Value(config.Host ?? string.Empty));
        table.AddRow(CliRender.Identifier("default-agent-id"), CliRender.Value(config.DefaultAgentId ?? string.Empty));
        table.AddRow(CliRender.Identifier("default-stream"), CliRender.Value(config.DefaultStream?.ToString() ?? string.Empty));
        CliRender.Separator("Configuration (3)");
        AnsiConsole.Write(table);
        return 0;
    }
}

public sealed class ConfigSetCommand(CliConfigStore configStore) : AsyncCommand<ConfigSetCommand.Settings>
{
    public sealed class Settings : OscovaCommandSettings
    {
        [CommandArgument(0, "<KEY>")]
        [Description("Setting to write: host, default-agent-id, or default-stream.")]
        public string Key { get; init; } = string.Empty;

        [CommandArgument(1, "<VALUE>")]
        [Description("New value for the setting. Use true or false for default-stream.")]
        public string Value { get; init; } = string.Empty;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        CliOutput.Configure(settings);
        var config = configStore.Read();
        switch (settings.Key.ToLowerInvariant())
        {
            case "host":
                config.Host = CliHostResolver.NormalizeHostUrl(settings.Value);
                break;
            case "defaultagentid":
            case "default-agent-id":
                config.DefaultAgentId = string.IsNullOrWhiteSpace(settings.Value) ? null : settings.Value.Trim();
                break;
            case "defaultstream":
            case "default-stream":
                if (!bool.TryParse(settings.Value, out var stream))
                {
                    throw new CliExitException(4, "defaultStream must be true or false.");
                }

                config.DefaultStream = stream;
                break;
            default:
                throw new CliExitException(4, $"Unknown config key '{settings.Key}'.");
        }

        configStore.Write(config);
        // Serialize the saved configuration for machine callers.
        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(config, cancellationToken).ConfigureAwait(false);
        }
        else if (!settings.Quiet &&
            AnsiConsole.Profile.Capabilities.Interactive && AnsiConsole.Profile.Capabilities.Ansi)
        {
            var value = settings.Key.ToLowerInvariant() switch
            {
                "host" => config.Host,
                "defaultagentid" or "default-agent-id" => config.DefaultAgentId,
                _ => config.DefaultStream?.ToString()
            };
            CliRender.KeyValue("Config saved",
            [
                ("Setting", CliRender.Identifier(settings.Key)),
                ("Value", CliRender.Value(value ?? string.Empty))
            ]);
        }
        else
        {
            CliOutput.Info("Config saved.", settings.Quiet);
        }

        return 0;
    }
}
