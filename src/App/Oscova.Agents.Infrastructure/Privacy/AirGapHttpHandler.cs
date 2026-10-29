using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Policies;

namespace Oscova.Agents.Infrastructure.Privacy;

/// <summary>
/// Blocks host-managed HTTP clients from reaching public endpoints during an air-gapped run.
/// </summary>
public sealed class AirGapHttpHandler(
    RunContextAccessor runContextAccessor,
    ILogger<AirGapHttpHandler> logger) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        if (runContextAccessor.Current?.AirGapped == true &&
            uri is not null &&
            !EgressClassifier.IsLocalHost(uri))
        {
            logger.LogWarning("Offline mode blocked outbound request to {Host}.", uri.Host);
            throw new HttpRequestException($"Offline mode is on: outbound request to '{uri.Host}' was blocked.");
        }

        return base.SendAsync(request, cancellationToken);
    }
}
