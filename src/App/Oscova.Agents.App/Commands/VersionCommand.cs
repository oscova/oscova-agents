using System.Net.Http.Json;
using System.Reflection;
using Oscova.Agents.App.Cli;
using Oscova.Agents.Core.ApiContracts.HostControl;
using Oscova.Agents.Host.Constants;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Oscova.Agents.App.Commands;

/// <summary>
/// Shows the CLI version and, when the host answers, the host and framework versions.
/// </summary>
public sealed class VersionCommand(CliHostResolver resolver, HostApiClient apiClient) : AsyncCommand<OscovaCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, OscovaCommandSettings settings, CancellationToken cancellationToken)
    {
        CliOutput.Configure(settings);
        var cliVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "dev";
        var hostVersion = await ReadHostVersionAsync(resolver, apiClient, settings, cancellationToken).ConfigureAwait(false);

        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(new { cli = cliVersion, host = hostVersion }, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var host = hostVersion?.Version;
        var framework = hostVersion?.FrameworkVersion;
        var compatibility = hostVersion?.ApiCompatibility;
        CliRender.KeyValue("Version",
        [
            ("CLI", CliRender.Value(cliVersion)),
            ("Host", host is null
                ? new Text("not reachable - run 'oscova start'", new Style(CliTheme.Warning))
                : CliRender.Value(host)),
            ("Framework", framework is null
                ? new Text("-", CliTheme.Label)
                : CliRender.Value(framework)),
            ("API compatibility", compatibility is null
                ? new Text("-", CliTheme.Label)
                : CliRender.Value(compatibility))
        ]);

        return 0;
    }

    /// <summary>
    /// Reads the host version, returning null when the host is not reachable rather than failing the command.
    /// </summary>
    internal static async Task<HostVersionResponse?> ReadHostVersionAsync(
        CliHostResolver resolver,
        HostApiClient apiClient,
        OscovaCommandSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            var host = await resolver.ResolveAsync(settings, requireToken: false, cancellationToken).ConfigureAwait(false);
            using var client = apiClient.Create(host);
            return await client.GetFromJsonAsync<HostVersionResponse>(ApiRoutes.HostControl.Version, CliJson.Options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or CliExitException or TaskCanceledException)
        {
            return null;
        }
    }
}
