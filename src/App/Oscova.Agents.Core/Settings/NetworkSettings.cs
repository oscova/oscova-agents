using Microsoft.Extensions.Logging;
using Oscova.Agents.Settings;

namespace Oscova.Agents.Core.Settings;

/// <summary>
/// Persists system-wide network access and public URL settings for this Oscova host.
/// </summary>
public sealed class NetworkSettings : SettingsBase
{
    public const string Key = "network";
    public const int DefaultPort = 11317;

    private bool _enableNetworkAccess;
    private bool _allowPrivateNetworkFetch;
    private bool _allowSandboxNetworkAccess;
    private int _port = DefaultPort;
    private PublicAppUrlMode _publicAppUrlMode = PublicAppUrlMode.AutoNetwork;
    private string _customPublicAppUrl = string.Empty;

    public NetworkSettings(IAppSettingsRepository repository, ILoggerFactory loggerFactory)
        : base(repository, WellKnownUsers.SystemId, loggerFactory) { }

    public override string SettingsKey => Key;

    public bool EnableNetworkAccess
    {
        get => _enableNetworkAccess;
        set => SetProperty(ref _enableNetworkAccess, value);
    }

    /// <summary>
    /// Allows web__fetch to reach loopback and private-network URLs while keeping reserved ranges blocked.
    /// </summary>
    public bool AllowPrivateNetworkFetch
    {
        get => _allowPrivateNetworkFetch;
        set => SetProperty(ref _allowPrivateNetworkFetch, value);
    }

    /// <summary>Allows the selected container provider to use networking for ordinary online runs.</summary>
    public bool AllowSandboxNetworkAccess
    {
        get => _allowSandboxNetworkAccess;
        set => SetProperty(ref _allowSandboxNetworkAccess, value);
    }

    public int Port
    {
        get => _port;
        set => SetProperty(ref _port, NormalizePort(value));
    }

    public PublicAppUrlMode PublicAppUrlMode
    {
        get => _publicAppUrlMode;
        set => SetProperty(ref _publicAppUrlMode, value);
    }

    public string CustomPublicAppUrl
    {
        get => _customPublicAppUrl;
        set => SetProperty(ref _customPublicAppUrl, NormalizePublicUrl(value));
    }

    /// <summary>
    /// Creates a detached data snapshot for API responses and import/export.
    /// </summary>
    public Snapshot CreateSnapshot()
    {
        return new Snapshot
        {
            EnableNetworkAccess = EnableNetworkAccess,
            AllowPrivateNetworkFetch = AllowPrivateNetworkFetch,
            AllowSandboxNetworkAccess = AllowSandboxNetworkAccess,
            Port = Port,
            PublicAppUrlMode = PublicAppUrlMode,
            CustomPublicAppUrl = CustomPublicAppUrl
        };
    }

    /// <summary>
    /// Applies values from a detached snapshot onto this settings instance.
    /// </summary>
    public void ApplyFrom(Snapshot data)
    {
        EnableNetworkAccess = data.EnableNetworkAccess;
        AllowPrivateNetworkFetch = data.AllowPrivateNetworkFetch;
        AllowSandboxNetworkAccess = data.AllowSandboxNetworkAccess;
        Port = data.Port;
        PublicAppUrlMode = data.PublicAppUrlMode;
        CustomPublicAppUrl = data.CustomPublicAppUrl;
    }

    public override string ToYaml()
    {
        return Yaml.Serialize(CreateSnapshot());
    }

    public override void FromYaml(string yaml)
    {
        var data = Yaml.Deserialize<Snapshot>(yaml);
        if (data is not null)
        {
            ApplyFrom(data);
        }
    }

    // Silent normalization is intentional — range validation belongs at the input boundary.
    public static int NormalizePort(int port) =>
        port is >= 1 and <= 65535 ? port : DefaultPort;

    public static string NormalizePublicUrl(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().TrimEnd('/');

    /// <summary>
    /// Plain data object for serialization and settings UI drafts.
    /// </summary>
    public sealed class Snapshot
    {
        public bool EnableNetworkAccess { get; set; }
        public bool AllowPrivateNetworkFetch { get; set; }
        public bool AllowSandboxNetworkAccess { get; set; }
        public int Port { get; set; } = DefaultPort;
        public PublicAppUrlMode PublicAppUrlMode { get; set; } = PublicAppUrlMode.AutoNetwork;
        public string CustomPublicAppUrl { get; set; } = string.Empty;
    }
}

/// <summary>
/// Selects how Oscova should publish a URL for external integrations.
/// </summary>
public enum PublicAppUrlMode
{
    AutoNetwork = 0,
    Custom = 1
}
