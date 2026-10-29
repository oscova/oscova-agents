using Microsoft.Kiota.Abstractions;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Harness;

namespace Oscova.Agents.Plugins.MicrosoftGraph;

/// <summary>Preserves safe tool failures while retaining the provider exception in logs.</summary>
internal static class MicrosoftGraphExecution
{
    internal static async Task<string> ExecuteAsync(
        Func<Task<string>> action, ILogger logger, string service, string notConnectedMessage)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (ApiException exception) when (exception.ResponseStatusCode is 401 or 403)
        {
            logger.LogError(exception, "{Service} request was unauthorized.", service);
            throw new ToolFailureException(notConnectedMessage);
        }
        catch (ApiException exception)
        {
            logger.LogError(exception, "{Service} request failed.", service);
            throw new ToolFailureException($"The {service} request failed.", "Check the connection or try again later.");
        }
    }
}
