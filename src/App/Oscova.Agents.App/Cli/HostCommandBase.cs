using System.Net;
using Spectre.Console.Cli;

namespace Oscova.Agents.App.Cli;

/// <summary>
/// Base for commands that call the host API, handling console setup, host resolution, and client creation.
/// </summary>
public abstract class HostCommandBase<TSettings>(CliHostResolver resolver, HostApiClient apiClient) : AsyncCommand<TSettings>
    where TSettings : OscovaCommandSettings
{
    /// <summary>
    /// Whether the command needs a CLI login before it can run.
    /// </summary>
    protected virtual bool RequireToken => true;

    protected abstract Task<int> RunAsync(HttpClient client, TSettings settings, CancellationToken cancellationToken);

    public override async Task<int> ExecuteAsync(CommandContext context, TSettings settings, CancellationToken cancellationToken)
    {
        CliOutput.Configure(settings);
        var host = await resolver.ResolveAsync(settings, RequireToken, cancellationToken).ConfigureAwait(false);
        using var client = apiClient.Create(host);

        try
        {
            return await RunAsync(client, settings, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode status)
        {
            // Helpers such as GetFromJsonAsync throw before a command can inspect the response,
            // so the exit-code contract is applied here rather than at every call site.
            throw new CliExitException(CliHttpErrors.MapExitCode(status), $"The host at {host.HostUrl} answered HTTP {(int)status}.");
        }
    }
}
