using Oscova.Agents.Host.Hosting;

namespace Oscova.Agents.App.Cli;

public sealed class StartCollisionChecker(IHttpClientFactory httpClientFactory)
{
    public async Task<RuntimeDescriptor?> FindRunningHostAsync(CancellationToken cancellationToken)
    {
        return await RuntimeDescriptorWriter.TryReadLiveAsync(httpClientFactory.CreateClient(), cancellationToken).ConfigureAwait(false);
    }
}