using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Mcp;
using Oscova.Agents.Execution;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Infrastructure.Mcp;

/// <summary>Creates the same native, HTTP or container MCP transport on its authorized execution device.</summary>
internal static class McpTransportFactory
{
    /// <summary>Uses explicit launch values and mounts without inheriting another account's environment.</summary>
    public static async Task<IClientTransport> CreateAsync(McpServerConfig server,
        IReadOnlyList<AllowedDirectory> directories, IExecutionEnvironmentCatalog executionEnvironments,
        IHttpClientFactory httpClients, ILogger logger, TimeSpan shutdownTimeout, CancellationToken ct)
    {
        if (server.Transport == McpTransportType.Http)
        {
            return new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = server.Name,
                Endpoint = new Uri(server.Endpoint!, UriKind.Absolute),
                AdditionalHeaders = server.Headers is null ? null :
                    new Dictionary<string, string>(server.Headers, StringComparer.OrdinalIgnoreCase)
            }, httpClients.CreateClient(), ownsHttpClient: false);
        }

        if (server.Transport != McpTransportType.Stdio)
        {
            throw new ArgumentException("Select HTTP or stdio for the MCP transport.");
        }

        if (server.ExecutionEnvironment == McpExecutionEnvironment.Containerized)
        {
            var launch = await executionEnvironments.GetContainerLauncher().PrepareAsync(new(server.Name, server.Command!, server.Arguments?.ToArray() ?? [],
                new Dictionary<string, string?>(server.EnvironmentVariables ?? new Dictionary<string, string?>()), directories,
                new RunContextAccessor().Current?.AirGapped == true), ct).ConfigureAwait(false);
            return new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = server.Name,
                Command = launch.Command,
                ShutdownTimeout = shutdownTimeout,
                Arguments = launch.Arguments.ToArray(),
                WorkingDirectory = launch.WorkingDirectory,
                EnvironmentVariables = launch.Environment?
                    .Where(variable => variable.Value is not null)
                    .ToDictionary(variable => variable.Key, variable => variable.Value, StringComparer.OrdinalIgnoreCase)
            });
        }

        if (server.ExecutionEnvironment != McpExecutionEnvironment.HostLocal)
        {
            throw new ArgumentException("Select native or containerized MCP execution.");
        }

        var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        // Docker Desktop needs this Windows directory, which the SDK's curated defaults omit.
        if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("ProgramData") is { } programData)
        {
            environment["ProgramData"] = programData;
        }

        foreach (var (key, configured) in server.EnvironmentVariables ?? new Dictionary<string, string?>())
        {
            var value = configured ?? Environment.GetEnvironmentVariable(key);
            if (value is not null) { environment[key] = value; }
        }

        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = server.Name,
            Command = server.Command!,
            Arguments = server.Arguments?.ToArray() ?? [],
            WorkingDirectory = server.WorkingDirectory,
            ShutdownTimeout = shutdownTimeout,
            InheritEnvironmentVariables = false,
            EnvironmentVariables = environment
        });
    }
}
