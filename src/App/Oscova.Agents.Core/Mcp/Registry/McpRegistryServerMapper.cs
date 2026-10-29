using Oscova.Agents.Core.ApiContracts.Mcp.Registry;

namespace Oscova.Agents.Core.Mcp.Registry;

/// <summary>
/// Converts official MCP Registry server metadata into Oscova MCP server editor presets.
/// </summary>
public static class McpRegistryServerMapper
{
    /// <summary>
    /// Maps one registry server into a UI catalog entry with all usable install options.
    /// </summary>
    public static McpRegistryCatalogEntry Map(McpRegistryServer server)
    {
        var entry = new McpRegistryCatalogEntry
        {
            Name = server.Name,
            Title = server.Title,
            Description = server.Description ?? string.Empty,
            Version = server.Version,
            WebsiteUrl = server.WebsiteUrl,
            RepositoryUrl = server.Repository?.Url
        };

        AddRemoteOptions(server, entry);
        AddPackageOptions(server, entry);

        // Every returned option must select exactly one endpoint or package through the install tool.
        foreach (var group in entry.Options.GroupBy(option => option.Kind, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            var index = 0;
            foreach (var option in group) { option.Kind = $"{group.Key}-{++index}"; }
        }

        if (entry.Options.Count == 0)
        {
            entry.Options.Add(new McpRegistryInstallOption
            {
                Kind = "manual",
                Label = "Manual setup",
                IsManual = true
            });
        }

        return entry;
    }

    private static void AddRemoteOptions(McpRegistryServer server, McpRegistryCatalogEntry entry)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var remote in server.Remotes ?? [])
        {
            if (string.IsNullOrWhiteSpace(remote.Url))
            {
                continue;
            }

            var kind = string.Equals(remote.Type, "sse", StringComparison.OrdinalIgnoreCase)
                ? "remote-sse"
                : "remote-http";
            var config = BuildRemote(server, remote, out var required);
            if (!seen.Add(BuildServerSignature(config)))
            {
                continue;
            }

            entry.Options.Add(new McpRegistryInstallOption
            {
                Kind = kind,
                Label = FormatRemoteLabel(remote),
                Server = config,
                RequiredFields = required
            });
        }
    }

    private static McpServerConfig BuildRemote(
        McpRegistryServer server,
        McpRegistryRemote remote,
        out List<string> required)
    {
        required = [];
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in remote.Headers ?? [])
        {
            if (string.IsNullOrWhiteSpace(header.Name))
            {
                continue;
            }

            headers[header.Name] = header.Value ?? string.Empty;
            if (header.IsSecret || string.IsNullOrWhiteSpace(header.Value))
            {
                required.Add(header.Name);
            }
        }

        return new McpServerConfig
        {
            Name = ResolveServerName(server),
            Transport = McpTransportType.Http,
            Endpoint = remote.Url,
            Headers = headers.Count == 0 ? null : headers
        };
    }

    private static void AddPackageOptions(McpRegistryServer server, McpRegistryCatalogEntry entry)
    {
        foreach (var package in server.Packages ?? [])
        {
            if (string.IsNullOrWhiteSpace(package.RuntimeHint))
            {
                continue;
            }

            var config = BuildStdio(server, package, out var required);
            if (config is null)
            {
                continue;
            }

            entry.Options.Add(new McpRegistryInstallOption
            {
                Kind = package.RuntimeHint,
                Label = $"Local ({package.RuntimeHint})",
                Server = config,
                RequiredFields = required
            });
        }
    }

    private static McpServerConfig? BuildStdio(
        McpRegistryServer server,
        McpRegistryPackage package,
        out List<string> required)
    {
        required = [];

        if (string.IsNullOrWhiteSpace(package.RuntimeHint))
        {
            return null;
        }

        var arguments = new List<string>();
        AddArguments(package.RuntimeArguments, arguments, required);

        var identifier = FormatIdentifier(package);
        if (!string.IsNullOrWhiteSpace(identifier))
        {
            arguments.Add(identifier);
        }

        AddArguments(package.PackageArguments, arguments, required);

        var environmentVariables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var variable in package.EnvironmentVariables ?? [])
        {
            if (string.IsNullOrWhiteSpace(variable.Name))
            {
                continue;
            }

            environmentVariables[variable.Name] = variable.Value ?? variable.Default ?? string.Empty;
            if (variable.IsSecret || variable.IsRequired && string.IsNullOrWhiteSpace(variable.Default))
            {
                required.Add(variable.Name);
            }
        }

        return new McpServerConfig
        {
            Name = ResolveServerName(server),
            Transport = McpTransportType.Stdio,
            Command = package.RuntimeHint,
            Arguments = arguments.Count == 0 ? null : arguments,
            EnvironmentVariables = environmentVariables.Count == 0 ? null : environmentVariables
        };
    }

    private static void AddArguments(
        IEnumerable<McpRegistryArgument>? registryArguments,
        List<string> arguments,
        List<string> required)
    {
        foreach (var argument in registryArguments ?? [])
        {
            var isNamed = string.Equals(argument.Type, "named", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(argument.Value))
            {
                if (isNamed && !string.IsNullOrWhiteSpace(argument.Name))
                {
                    arguments.Add(argument.Name);
                }

                arguments.Add(argument.Value);
                continue;
            }

            var requiredName = isNamed
                ? argument.Name
                : argument.ValueHint;
            if (!string.IsNullOrWhiteSpace(requiredName))
            {
                required.Add(requiredName);
            }
        }
    }

    private static string? FormatIdentifier(McpRegistryPackage package)
    {
        if (string.IsNullOrWhiteSpace(package.Identifier))
        {
            return null;
        }

        return string.Equals(package.RegistryType, "npm", StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(package.Version)
            ? $"{package.Identifier}@{package.Version}"
            : package.Identifier;
    }

    private static string FormatRemoteLabel(McpRegistryRemote remote)
    {
        var endpoint = FormatEndpoint(remote.Url);
        return string.Equals(remote.Type, "sse", StringComparison.OrdinalIgnoreCase)
            ? $"Remote (SSE) - {endpoint}"
            : $"Remote (HTTP) - {endpoint}";
    }

    private static string FormatEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return "endpoint";
        }

        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            ? string.IsNullOrWhiteSpace(uri.PathAndQuery) || string.Equals(uri.PathAndQuery, "/", StringComparison.Ordinal)
                ? uri.Host
                : $"{uri.Host}{uri.PathAndQuery}"
            : endpoint;
    }

    private static string BuildServerSignature(McpServerConfig server)
    {
        var arguments = server.Arguments is null ? string.Empty : string.Join('\u001f', server.Arguments);
        return string.Join(
            '\u001e',
            server.Transport.ToString(),
            server.Endpoint ?? string.Empty,
            server.Command ?? string.Empty,
            arguments);
    }

    private static string ResolveServerName(McpRegistryServer server)
    {
        if (!string.IsNullOrWhiteSpace(server.Title))
        {
            return server.Title;
        }

        var lastSlash = server.Name.LastIndexOf('/');
        if (lastSlash >= 0 && lastSlash < server.Name.Length - 1)
        {
            return server.Name[(lastSlash + 1)..];
        }

        var lastDot = server.Name.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < server.Name.Length - 1)
        {
            return server.Name[(lastDot + 1)..];
        }

        return string.IsNullOrWhiteSpace(server.Name) ? "MCP server" : server.Name;
    }
}
