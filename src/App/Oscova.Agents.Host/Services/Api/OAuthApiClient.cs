using Oscova.Agents.Core.ApiContracts.OAuth;
using Oscova.Agents.Host.Constants;

namespace Oscova.Agents.Host.Services.Api;

public sealed class OAuthApiClient(HttpClient httpClient) : ApiClientBase
{
    public async Task<OAuthStatusResponse> GetStatusAsync(CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<OAuthStatusResponse>(ApiRoutes.OAuth.Status, ct)
.ConfigureAwait(false) ?? new OAuthStatusResponse();
    }

    public async Task DisconnectAsync(string provider, CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsync(ApiRoutes.OAuth.Disconnect(provider), content: null, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }
}
