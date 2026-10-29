using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Mcp.Registry;

namespace Oscova.Agents.Infrastructure.Mcp.Registry;

/// <summary>
/// Reads the official MCP Registry search API and returns latest active server definitions.
/// </summary>
public sealed class McpRegistryService(
    IHttpClientFactory httpClientFactory,
    ILogger<McpRegistryService> logger) : IMcpRegistryService
{
    private const string ClientName = "mcp-registry";
    private const int SearchLimit = 100;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <inheritdoc />
    public async Task<IReadOnlyList<McpRegistryServer>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var requestUri = $"v0.1/servers?search={Uri.EscapeDataString(query.Trim())}&limit={SearchLimit}";

        try
        {
            var client = httpClientFactory.CreateClient(ClientName);
            using var response = await client.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var envelope = await JsonSerializer.DeserializeAsync<McpRegistrySearchEnvelope>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);

            return SelectLatestActive(envelope?.Servers);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "MCP registry search failed for query {Query}.", query);
            throw new McpRegistryException("The MCP Registry could not be searched.", ex);
        }
    }

    private static List<McpRegistryServer> SelectLatestActive(IEnumerable<McpRegistrySearchRow>? rows)
    {
        var byName = new Dictionary<string, McpRegistrySearchRow>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows ?? [])
        {
            if (row.Server is null || string.IsNullOrWhiteSpace(row.Server.Name) || !IsActive(row))
            {
                continue;
            }

            if (!byName.TryGetValue(row.Server.Name, out var existing) ||
                !IsLatest(existing) && IsLatest(row))
            {
                byName[row.Server.Name] = row;
            }
        }

        return [.. byName.Values
            .Select(row => row.Server!)
            .OrderBy(server => server.Title ?? server.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static bool IsActive(McpRegistrySearchRow row) =>
        string.IsNullOrWhiteSpace(row.Meta?.Official?.Status) ||
        string.Equals(row.Meta.Official.Status, "active", StringComparison.OrdinalIgnoreCase);

    private static bool IsLatest(McpRegistrySearchRow row) => row.Meta?.Official?.IsLatest == true;
}

internal sealed record McpRegistrySearchEnvelope(
    [property: JsonPropertyName("servers")] List<McpRegistrySearchRow>? Servers);

internal sealed record McpRegistrySearchRow(
    [property: JsonPropertyName("server")] McpRegistryServer? Server,
    [property: JsonPropertyName("_meta")] McpRegistryRowMeta? Meta);

internal sealed record McpRegistryRowMeta(
    [property: JsonPropertyName("io.modelcontextprotocol.registry/official")] McpRegistryOfficialMeta? Official);

internal sealed record McpRegistryOfficialMeta(
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("isLatest")] bool IsLatest);
