using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Plugins;

/// <summary>Provides narrow host services a plugin can use when creating runtime contributions.</summary>
public sealed class AgentPluginContext
{
    private static readonly HttpClient DefaultHttpClient = new();
    private readonly Func<string, string, string, CancellationToken, Task> _ensureDownloadAsync;

    /// <summary>Creates a plugin context with default HTTP, logging, and local time-zone services but without Host download support.</summary>
    /// <param name="credentialResolver">The resolver used for plugin-requested credentials.</param>
    /// <param name="pluginDirectory">The fully qualified directory containing the plugin manifest.</param>
    /// <param name="modelStorageDirectory">The fully qualified host-owned model storage directory.</param>
    public AgentPluginContext(
        IToolCredentialResolver credentialResolver,
        string pluginDirectory,
        string modelStorageDirectory)
        : this(
            credentialResolver,
            _ => DefaultHttpClient,
            NullLoggerFactory.Instance,
            () => TimeZoneInfo.Local,
            pluginDirectory,
            modelStorageDirectory,
            DownloadUnavailableAsync)
    {
    }

    /// <summary>Creates a plugin context from host-provided services.</summary>
    /// <param name="credentialResolver">The resolver used for plugin-requested credentials.</param>
    /// <param name="httpClientFactory">The factory that creates named HTTP clients for plugin dependencies.</param>
    /// <param name="loggerFactory">The logger factory exposed to plugin contributions.</param>
    /// <param name="userTimeZoneResolver">Resolves the user's current preferred time zone for user-facing timestamps.</param>
    /// <param name="pluginDirectory">The fully qualified directory containing the plugin manifest.</param>
    /// <param name="modelStorageDirectory">The fully qualified host-owned model storage directory.</param>
    /// <param name="ensureDownloadAsync">Starts or joins a Host-tracked plugin asset download.</param>
    public AgentPluginContext(
        IToolCredentialResolver credentialResolver,
        Func<string, HttpClient> httpClientFactory,
        ILoggerFactory loggerFactory,
        Func<TimeZoneInfo> userTimeZoneResolver,
        string pluginDirectory,
        string modelStorageDirectory,
        Func<string, string, string, CancellationToken, Task> ensureDownloadAsync)
    {
        ArgumentNullException.ThrowIfNull(credentialResolver);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(userTimeZoneResolver);
        ArgumentNullException.ThrowIfNull(ensureDownloadAsync);

        if (string.IsNullOrWhiteSpace(pluginDirectory) || !Path.IsPathFullyQualified(pluginDirectory))
        {
            throw new ArgumentException("Plugin directory must be a fully qualified path.", nameof(pluginDirectory));
        }

        if (string.IsNullOrWhiteSpace(modelStorageDirectory) || !Path.IsPathFullyQualified(modelStorageDirectory))
        {
            throw new ArgumentException("Model storage directory must be a fully qualified path.", nameof(modelStorageDirectory));
        }

        CredentialResolver = credentialResolver;
        _httpClientFactory = httpClientFactory;
        _ensureDownloadAsync = ensureDownloadAsync;
        LoggerFactory = loggerFactory;
        _userTimeZoneResolver = userTimeZoneResolver;
        PluginDirectory = Path.GetFullPath(pluginDirectory);
        ModelStorageDirectory = Path.GetFullPath(modelStorageDirectory);
    }

    private readonly Func<string, HttpClient> _httpClientFactory;
    private readonly Func<TimeZoneInfo> _userTimeZoneResolver;

    /// <summary>Resolves only the credential keys exposed to this plugin context.</summary>
    public IToolCredentialResolver CredentialResolver { get; }

    /// <summary>Creates host-managed HTTP clients for plugin contributions.</summary>
    /// <param name="name">The logical client name requested by the plugin.</param>
    /// <returns>An HTTP client supplied by the host integration.</returns>
    public HttpClient CreateHttpClient(string name) => _httpClientFactory(name);

    /// <summary>
    /// Creates plugin loggers without exposing the host service provider.
    /// </summary>
    public ILoggerFactory LoggerFactory { get; }

    /// <summary>Resolves the user-selected time zone for plugin-generated timestamps.</summary>
    public TimeZoneInfo UserTimeZone => _userTimeZoneResolver();

    /// <summary>Gets the read-only deployment directory for the current plugin.</summary>
    public string PluginDirectory { get; }

    /// <summary>Gets the host-owned root under which providers may store models.</summary>
    public string ModelStorageDirectory { get; }

    /// <summary>Starts or joins a Host-tracked download for a plugin-owned asset.</summary>
    /// <param name="id">The stable operation identifier shown by the Host.</param>
    /// <param name="url">The source URL selected by the provider or Host configuration.</param>
    /// <param name="destinationPath">The fully qualified destination under provider-owned model storage.</param>
    /// <param name="ct">Cancels the download request.</param>
    public Task EnsureDownloadAsync(
        string id,
        string url,
        string destinationPath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(destinationPath) || !Path.IsPathFullyQualified(destinationPath))
        {
            throw new ArgumentException("Download destination must be a fully qualified path.", nameof(destinationPath));
        }

        var fullDestinationPath = Path.GetFullPath(destinationPath);
        if (!PathHelper.IsPathWithinDirectory(fullDestinationPath, ModelStorageDirectory))
        {
            throw new ArgumentException("Download destination must be under model storage.", nameof(destinationPath));
        }

        // Keep plugin downloads on the Host tracker used by progress and cancellation endpoints.
        return _ensureDownloadAsync(id, url, fullDestinationPath, ct);
    }

    private static Task DownloadUnavailableAsync(
        string id,
        string url,
        string destinationPath,
        CancellationToken ct)
    {
        throw new NotSupportedException("This context was not created with Host download support.");
    }
}
