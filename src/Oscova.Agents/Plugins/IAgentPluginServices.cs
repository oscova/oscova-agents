using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Oscova.Agents.Toolsets;

namespace Oscova.Agents.Plugins;

/// <summary>Supplies guarded transport, tracked downloads, and owner-scoped account access.</summary>
public interface IAgentPluginServices
{
    /// <summary>Gets optional-use encryption, or throws NotSupportedException when the host does not supply it.</summary>
    IPluginEncryptionService Encryption { get; }
    /// <summary>Creates an HTTP client subject to the current run policy.</summary>
    HttpClient CreateHttpClient();
    /// <summary>Creates an HTTP handler subject to the current run policy.</summary>
    HttpMessageHandler CreateHttpMessageHandler();
    /// <summary>Starts or joins a Host-tracked asset download.</summary>
    Task EnsureDownloadAsync(PluginDownloadRequest request, CancellationToken cancellationToken = default);
    /// <summary>Resolves a token only for an account provider owned by this plugin.</summary>
    Task<string?> ResolveAccessTokenAsync(string providerId, CancellationToken cancellationToken = default);
    /// <summary>Saves oversized tool output for the current run, or null when the host offers no storage.</summary>
    IToolOutputStore? ToolOutputStore { get; }
}
