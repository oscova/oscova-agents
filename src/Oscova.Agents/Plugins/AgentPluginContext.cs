using System;
using System.Collections.Generic;
using Oscova.Agents.Workspace;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Utils;
using Oscova.Agents.Browsing;

namespace Oscova.Agents.Plugins;

/// <summary>Provides narrow host services a plugin can use when creating runtime contributions.</summary>
public sealed class AgentPluginContext
{
    /// <summary>Resolves the active invocation's authorized directories on this execution device.</summary>
    public Func<IReadOnlyList<AllowedDirectory>> GetAllowedDirectories { get; init; } = () => [];

    /// <summary>Resolves the current invocation's authorized browser session, or is null in hosts without browsing.</summary>
    public Func<CancellationToken, Task<BrowserSessionScope>>? ResolveBrowserSessionAsync { get; init; }

    /// <summary>Routes tool commands through the host's authorized chat restoration and selection.</summary>
    public Func<BrowserCommand, CancellationToken, Task<BrowserResult>>? ExecuteBrowserCommandAsync { get; init; }

    /// <summary>Creates a plugin context from host-provided services.</summary>
    /// <param name="credentialResolver">The resolver used for plugin-requested credentials.</param>
    /// <param name="services">The Host services available to this plugin.</param>
    /// <param name="loggerFactory">The logger factory exposed to plugin contributions.</param>
    /// <param name="timeProvider">Resolves the user's current preferred time zone for user-facing timestamps.</param>
    /// <param name="pluginDirectory">The fully qualified directory containing the plugin manifest.</param>
    /// <param name="modelStorageDirectory">The fully qualified host-owned model storage directory.</param>
    public AgentPluginContext(
        IToolCredentialResolver credentialResolver,
        IAgentPluginServices services,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        string pluginDirectory,
        string modelStorageDirectory)
    {
        ArgumentNullException.ThrowIfNull(credentialResolver);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (string.IsNullOrWhiteSpace(pluginDirectory) || !Path.IsPathFullyQualified(pluginDirectory))
        {
            throw new ArgumentException("Plugin directory must be a fully qualified path.", nameof(pluginDirectory));
        }

        if (string.IsNullOrWhiteSpace(modelStorageDirectory) || !Path.IsPathFullyQualified(modelStorageDirectory))
        {
            throw new ArgumentException("Model storage directory must be a fully qualified path.", nameof(modelStorageDirectory));
        }

        CredentialResolver = credentialResolver;
        Services = services;
        LoggerFactory = loggerFactory;
        TimeProvider = timeProvider;
        PluginDirectory = Path.GetFullPath(pluginDirectory);
        ModelStorageDirectory = Path.GetFullPath(modelStorageDirectory);
    }

    /// <summary>Resolves only the credential keys exposed to this plugin context.</summary>
    public IToolCredentialResolver CredentialResolver { get; }

    /// <summary>Resolves a usable token for a provider declared by this plugin.</summary>
    public Task<string?> ResolveAccessTokenAsync(string providerId, CancellationToken ct = default)
        => Services.ResolveAccessTokenAsync(providerId, ct);

    /// <summary>Creates host-managed HTTP clients for plugin contributions.</summary>
    /// <returns>An HTTP client supplied by the host integration.</returns>
    public HttpClient CreateHttpClient() => Services.CreateHttpClient();

    /// <summary>Gets the guarded Host services available to the plugin.</summary>
    public IAgentPluginServices Services { get; }

    /// <summary>Gets the Host time provider for this context.</summary>
    public TimeProvider TimeProvider { get; }

    /// <summary>
    /// Creates plugin loggers without exposing the host service provider.
    /// </summary>
    public ILoggerFactory LoggerFactory { get; }

    /// <summary>Resolves the user-selected time zone for plugin-generated timestamps.</summary>
    public TimeZoneInfo UserTimeZone => TimeProvider.LocalTimeZone;

    /// <summary>Gets the read-only deployment directory for the current plugin.</summary>
    public string PluginDirectory { get; }

    /// <summary>Gets the host-owned root under which providers may store models.</summary>
    public string ModelStorageDirectory { get; }

    /// <summary>Reads installation settings through the owning plugin's admitted storage namespace.</summary>
    public IPluginSettings? InstallationSettings { get; init; }

    /// <summary>Gets optional settings storage bound to the authenticated user and owning plugin.</summary>
    public IPluginSettings? UserSettings { get; init; }

    /// <summary>Gets the private writable root owned by this plugin on this execution device.</summary>
    public string? PrivateDataDirectory { get; init; }

    /// <summary>Gets the current user's data root, or null when this context has no user.</summary>
    public string? UserDataDirectory { get; init; }

    /// <summary>Gets the current user's settings directory, or null when this context has no user.</summary>
    public string? UserSettingsDirectory { get; init; }

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
        return Services.EnsureDownloadAsync(new PluginDownloadRequest(id, url, fullDestinationPath), ct);
    }
}
