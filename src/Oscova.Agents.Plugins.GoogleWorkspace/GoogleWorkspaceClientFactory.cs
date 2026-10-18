using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;

namespace Oscova.Agents.Plugins.GoogleWorkspace;

/// <summary>Builds Google SDK clients from a caller-managed usable access token.</summary>
internal static class GoogleWorkspaceClientFactory
{
    internal static async Task<BaseClientService.Initializer?> CreateInitializerAsync(AgentPluginContext context, CancellationToken ct)
    {
        var token = await context.ResolveAccessTokenAsync(GoogleWorkspaceProvider.Id, ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(token) ? null : new BaseClientService.Initializer
        {
            HttpClientInitializer = GoogleCredential.FromAccessToken(token),
            // The invoking tool owns both the service and this factory's non-owned handler chain.
            HttpClientFactory = new Google.Apis.Http.HttpClientFromMessageHandlerFactory(_ =>
                new Google.Apis.Http.HttpClientFromMessageHandlerFactory.ConfiguredHttpMessageHandler(
                    context.Services.CreateHttpMessageHandler(), performsAutomaticDecompression: false, handlesRedirect: false)),
            ApplicationName = "Oscova Assistant"
        };
    }
}
