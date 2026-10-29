using Oscova.Agents.Core.ApiContracts.Nodes;
using Oscova.Agents.Host.Constants;

namespace Oscova.Agents.Host.Services.Api;

/// <summary>Loads the signed-in account's devices through the central API.</summary>
public sealed class NodesApiClient(HttpClient httpClient) : ApiClientBase
{
    /// <summary>Requests another preparation attempt and preserves actionable server error details.</summary>
    public async Task RetryPluginAsync(string pluginId, CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsync($"api/nodes/plugins/{Uri.EscapeDataString(pluginId)}/retry", null, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    /// <summary>Loads current device availability and the account's execution permissions.</summary>
    public async Task<NodeCatalog> GetAsync(CancellationToken ct = default)
    {
        using var response = await httpClient.GetAsync(ApiRoutes.Nodes.Root, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<NodeCatalog>(ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("The server returned no device catalog.");
    }

}
