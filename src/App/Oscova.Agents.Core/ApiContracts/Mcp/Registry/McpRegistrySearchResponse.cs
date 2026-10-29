using Oscova.Agents.Core.Mcp;

namespace Oscova.Agents.Core.ApiContracts.Mcp.Registry;

/// <summary>
/// Catalog response returned by Oscova's MCP registry search endpoint.
/// </summary>
public sealed class McpRegistrySearchResponse
{
    public IReadOnlyList<McpRegistryCatalogEntry> Entries { get; set; } = [];
    public string? Error { get; set; }
}

/// <summary>
/// Search result entry with install options ready for the MCP server editor.
/// </summary>
public sealed class McpRegistryCatalogEntry
{
    public required string Name { get; set; }
    public string? Title { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string? WebsiteUrl { get; set; }
    public string? RepositoryUrl { get; set; }
    public IList<McpRegistryInstallOption> Options { get; set; } = [];
}

/// <summary>
/// One registry-published way to add an MCP server to the user's configuration.
/// </summary>
public sealed class McpRegistryInstallOption
{
    public required string Kind { get; set; }
    public required string Label { get; set; }
    public bool IsManual { get; set; }
    public McpServerConfig? Server { get; set; }
    public IReadOnlyList<string> RequiredFields { get; set; } = [];
}
