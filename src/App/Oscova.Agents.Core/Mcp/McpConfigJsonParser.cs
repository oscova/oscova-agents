using System.Text.Json;

namespace Oscova.Agents.Core.Mcp;

public static class McpConfigJsonParser
{
    /// <summary>
    /// Parses external MCP server configuration JSON (VS Code, Cursor, Claude Desktop formats)
    /// into a list of <see cref="McpServerConfig"/> objects.
    /// </summary>
    public static IReadOnlyList<McpServerConfig> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        // Try known wrapper keys: "servers" (VS Code), "mcpServers" (Cursor / Claude Desktop)
        if (root.TryGetProperty("servers", out var serversObj) && serversObj.ValueKind == JsonValueKind.Object)
        {
            return ParseServerMap(serversObj);
        }

        if (root.TryGetProperty("mcpServers", out var mcpServersObj) && mcpServersObj.ValueKind == JsonValueKind.Object)
        {
            return ParseServerMap(mcpServersObj);
        }

        // Fallback: treat the root itself as a server map (bare { "name": { ... } })
        if (LooksLikeServerMap(root))
        {
            return ParseServerMap(root);
        }

        return [];
    }

    private static bool LooksLikeServerMap(JsonElement element)
    {
        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Object &&
                (prop.Value.TryGetProperty("command", out _) ||
                 prop.Value.TryGetProperty("url", out _) ||
                 prop.Value.TryGetProperty("endpoint", out _)))
            {
                return true;
            }
        }

        return false;
    }

    private static List<McpServerConfig> ParseServerMap(JsonElement serversElement)
    {
        var results = new List<McpServerConfig>();

        foreach (var prop in serversElement.EnumerateObject())
        {
            var serverName = prop.Name;
            var serverObj = prop.Value;

            if (serverObj.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var config = ParseSingleServer(serverName, serverObj);
            if (config is not null)
            {
                results.Add(config);
            }
        }

        return results;
    }

    private static McpServerConfig? ParseSingleServer(string name, JsonElement obj)
    {
        var config = new McpServerConfig
        {
            Id = Guid.NewGuid(),
            Name = name,
        };

        // Determine transport type
        var hasCommand = obj.TryGetProperty("command", out var commandEl);
        var hasUrl = obj.TryGetProperty("url", out var urlEl);
        var hasEndpoint = obj.TryGetProperty("endpoint", out var endpointEl);

        if (hasCommand)
        {
            config.Transport = McpTransportType.Stdio;
            config.Command = commandEl.GetString();

            // Arguments: "args" or "arguments"
            if (obj.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
            {
                config.Arguments = ParseStringArray(argsEl);
            }
            else if (obj.TryGetProperty("arguments", out var argsEl2) && argsEl2.ValueKind == JsonValueKind.Array)
            {
                config.Arguments = ParseStringArray(argsEl2);
            }

            // Working directory: "cwd" or "workingDirectory"
            if (obj.TryGetProperty("cwd", out var cwdEl))
            {
                config.WorkingDirectory = cwdEl.GetString();
            }
            else if (obj.TryGetProperty("workingDirectory", out var wdEl))
            {
                config.WorkingDirectory = wdEl.GetString();
            }

            // Environment variables: "env"
            if (obj.TryGetProperty("env", out var envEl) && envEl.ValueKind == JsonValueKind.Object)
            {
                config.EnvironmentVariables = ParseNullableStringDictionary(envEl);
            }
        }
        else if (hasUrl || hasEndpoint)
        {
            config.Transport = McpTransportType.Http;
            config.Endpoint = hasUrl ? urlEl.GetString() : endpointEl.GetString();

            // Headers
            if (obj.TryGetProperty("headers", out var headersEl) && headersEl.ValueKind == JsonValueKind.Object)
            {
                config.Headers = ParseStringDictionary(headersEl);
            }
        }
        else
        {
            // Can't determine transport type
            return null;
        }

        return config;
    }

    private static List<string> ParseStringArray(JsonElement arrayEl)
    {
        var list = new List<string>();
        foreach (var item in arrayEl.EnumerateArray())
        {
            var val = item.GetString();
            if (val is not null)
            {
                list.Add(val);
            }
        }

        return list;
    }

    private static Dictionary<string, string> ParseStringDictionary(JsonElement objEl)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in objEl.EnumerateObject())
        {
            var val = prop.Value.GetString();
            if (val is not null)
            {
                dict[prop.Name] = val;
            }
        }

        return dict;
    }

    private static Dictionary<string, string?> ParseNullableStringDictionary(JsonElement objEl)
    {
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in objEl.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.GetString();
        }

        return dict;
    }
}
