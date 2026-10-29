using System.Net;
using Microsoft.AspNetCore.SignalR.Client;

namespace Oscova.Agents.App.Cli;

/// <summary>Keeps an enrolled node reconnecting until stopped, unless its credential is rejected.</summary>
internal sealed class NodeReconnectPolicy : IRetryPolicy
{
    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        if (retryContext.RetryReason is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden })
        {
            return null;
        }

        return TimeSpan.FromSeconds(retryContext.PreviousRetryCount switch { 0 => 0, 1 => 2, 2 => 10, _ => 30 });
    }
}
