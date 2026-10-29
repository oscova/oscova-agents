using System.Security.Cryptography;
using Oscova.Agents.Core.Secrets;
using Oscova.Agents.Secrets;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.OAuth;
using Oscova.Agents.Infrastructure.OAuth;
using Oscova.Agents.Infrastructure.Utils;
using Oscova.Agents.Plugins;
using Oscova.Agents.Toolsets;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Infrastructure.Plugins;

/// <summary>Routes one plugin's transport, account and encryption requests through private Host policy.</summary>
internal sealed class PluginHostServices(
    string pluginId,
    IHttpClientFactory clients,
    IHttpMessageHandlerFactory handlers,
    IDownloadService downloads,
    OAuthProviderCatalog providers,
    IOAuthTokenService? tokens,
    IToolOutputStore outputStore,
    IContentProtector protector,
    string? userId) : IAgentPluginServices, IPluginEncryptionService
{
    internal const string GuardedClient = "PluginGuarded";
    internal const string ChannelTransport = "PluginChannelTransport";

    // Bind the purpose to the same case-insensitive identity used by the plugin catalog.
    private readonly string _encryptionPurpose = $"plugin\0{pluginId.ToLowerInvariant()}";
    private string EncryptionUserId => userId
        ?? throw new InvalidOperationException("Plugin encryption requires a user context.");

    public IToolOutputStore? ToolOutputStore => outputStore;
    public IPluginEncryptionService Encryption => this;

    /// <inheritdoc />
    public string Encrypt(string plaintext)
        => protector.Protect(EncryptionUserId, _encryptionPurpose, plaintext);

    /// <inheritdoc />
    public string Decrypt(string ciphertext)
    {
        var ownerId = EncryptionUserId;
        if (!protector.IsAvailable(ownerId))
        {
            throw new ConfidentialDataLockedException("Unlock your Vault Password before decrypting plugin data.");
        }

        return protector.Unprotect(ownerId, _encryptionPurpose, ciphertext)
            ?? throw new CryptographicException("Plugin data could not be decrypted for this user and plugin. The ciphertext may be damaged or belong to a different user or plugin.");
    }

    public HttpClient CreateHttpClient() => clients.CreateClient(GuardedClient);
    public HttpMessageHandler CreateHttpMessageHandler() => handlers.CreateHandler(GuardedClient);
    /// <summary>Rejects destinations outside model storage before invoking the Host download tracker.</summary>
    public Task EnsureDownloadAsync(PluginDownloadRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.DestinationPath) || !Path.IsPathFullyQualified(request.DestinationPath))
        {
            throw new ArgumentException("Download destination must be a fully qualified path.", nameof(request));
        }

        var destination = Path.GetFullPath(request.DestinationPath);
        if (!PathHelper.IsPathWithinDirectory(destination, PathUtil.ModelsPath))
        {
            throw new ArgumentException("Download destination must be under model storage.", nameof(request));
        }

        return downloads.EnsureDownloadAsync(request.Id, request.Url, destination, cancellationToken);
    }

    public Task<string?> ResolveAccessTokenAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var entry = providers.Get(providerId);
        if (!string.Equals(entry.PluginId, pluginId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("This plugin cannot access the requested account provider.");
        }

        return tokens is null ? Task.FromResult<string?>(null)
            : tokens.GetValidAccessTokenAsync(entry.Definition.Id, cancellationToken);
    }
}
