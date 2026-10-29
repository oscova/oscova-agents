using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;

namespace Oscova.Agents.Plugins.MicrosoftGraph;

/// <summary>Creates Graph clients that resolve a current Microsoft token for each request.</summary>
internal static class MicrosoftGraphClientFactory
{
    internal static async Task<GraphServiceClient?> CreateAsync(AgentPluginContext context, HttpClient transport, CancellationToken ct = default)
    {
        // Probe first so disconnected accounts fail before the SDK builds a request.
        var token = await context.ResolveAccessTokenAsync(MicrosoftGraphProvider.Id, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var authProvider = new BaseBearerTokenAuthenticationProvider(new MicrosoftGraphAccessTokenProvider(context));
        // The SDK leaves supplied transports owned by the operation that created them.
        return new GraphServiceClient(transport, authProvider);
    }

    /// <summary>Supplies current bearer tokens for Graph requests through the scoped host callback.</summary>
    private sealed class MicrosoftGraphAccessTokenProvider(AgentPluginContext context) : IAccessTokenProvider
    {
        public AllowedHostsValidator AllowedHostsValidator { get; } = new(["graph.microsoft.com"]);

        public async Task<string> GetAuthorizationTokenAsync(
            Uri uri, Dictionary<string, object>? additionalAuthenticationContext = null,
            CancellationToken cancellationToken = default)
        {
            return await context.ResolveAccessTokenAsync(MicrosoftGraphProvider.Id, cancellationToken).ConfigureAwait(false)
                ?? string.Empty;
        }
    }
}
