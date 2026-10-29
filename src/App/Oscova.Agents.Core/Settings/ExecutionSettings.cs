using Microsoft.Extensions.Logging;
using Oscova.Agents.Settings;

namespace Oscova.Agents.Core.Settings;

/// <summary>
/// Stores the selected provider for shell commands and bundled skill scripts.
/// </summary>
public sealed class ExecutionSettings : SettingsBase
{
    public const string Key = "execution";
    public const string ConfigurationKey = "execution-environment";

    private string? _providerId = "host";

    /// <summary>Stores the operator's installation-wide execution selection.</summary>
    public ExecutionSettings(IAppSettingsRepository repository, ILoggerFactory loggerFactory)
        : base(repository, WellKnownUsers.SystemId, loggerFactory)
    {
    }

    public override string SettingsKey => Key;

    public string? ProviderId
    {
        get => _providerId;
        set
        {
            if (value is not null && string.IsNullOrWhiteSpace(value))
            { throw new ArgumentException("ProviderId must be null or a nonempty provider identity.", nameof(value)); }

            SetProperty(ref _providerId, value);
        }
    }

    /// <inheritdoc />
    public override string ToYaml()
    {
        return Yaml.Serialize(new Snapshot
        {
            ProviderId = ProviderId
        });
    }

    /// <inheritdoc />
    public override void FromYaml(string yaml)
    {
        var snapshot = Yaml.Deserialize<Snapshot>(yaml);
        if (snapshot is null)
        {
            return;
        }

        ProviderId = snapshot.ProviderId;
    }

    private sealed class Snapshot
    {
        public string? ProviderId { get; set; } = "host";
    }
}
